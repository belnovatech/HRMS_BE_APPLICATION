using System.Text.Json.Nodes;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Api.Controllers;

[ApiController]
[Authorize(Roles = "hr")]
public sealed class FeatureActionsController(
    HrmsDbContext database,
    AccessScope access,
    IHttpClientFactory clients,
    IConfiguration configuration) : ControllerBase
{
    public sealed record StageRequest(string Stage);

    public sealed record DecisionRequest(string Status, string? Note);

    public sealed record DeviceAttendance(
        string Id,
        string EmployeeId,
        DateOnly Date,
        TimeOnly CheckIn,
        TimeOnly? CheckOut);

    [HttpPost("api/recruitment/candidates/{id}/stage")]
    public async Task<IActionResult> CandidateStage(
        string id,
        StageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Stage is not ("Applied" or "Screening" or "Interview" or "Offer" or "Hired" or "Rejected"))
        {
            return BadRequest();
        }

        FeatureResource? row = await Find("recruitment", "candidates", id, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        JsonObject body = JsonNode.Parse(row.Payload)!.AsObject();
        body["stage"] = request.Stage;
        row.Payload = body.ToJsonString();
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Ok(body);
    }

    [HttpPost("api/biometric/devices/{id}/sync")]
    public async Task<IActionResult> SyncDevice(string id, CancellationToken cancellationToken)
    {
        FeatureResource? row = await Find("biometric", "devices", id, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        string? endpoint = configuration["Biometric:GatewayUrl"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https")
        {
            return StatusCode(503, new
            {
                title = "A biometric HTTPS gateway must be configured before syncing."
            });
        }

        using HttpClient client = clients.CreateClient("biometric");
        if (configuration["Biometric:ApiKey"] is { Length: > 0 } key)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        }

        DeviceAttendance[] events;
        try
        {
            string requestPath = $"{uri.AbsoluteUri.TrimEnd('/')}/devices/{Uri.EscapeDataString(id)}/attendance";
            events = await client.GetFromJsonAsync<DeviceAttendance[]>(
                new Uri(requestPath),
                cancellationToken) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return StatusCode(502, new
            {
                title = "The biometric gateway did not return valid attendance data."
            });
        }

        if (events.Length > 10000)
        {
            return StatusCode(502, new
            {
                title = "The gateway batch is too large."
            });
        }

        int imported = 0;
        foreach (DeviceAttendance item in events.DistinctBy(item => item.Id))
        {
            string eventKey = $"{id}:{item.Id}";
            bool wasImported = await database.FeatureResources.AnyAsync(
                resource => resource.Area == "biometric" &&
                            resource.Collection == "imported" &&
                            resource.ExternalId == eventKey,
                cancellationToken);
            if (wasImported)
            {
                continue;
            }

            UserAccount? user = await database.Users.FindAsync([item.EmployeeId], cancellationToken);
            if (user is null || string.IsNullOrWhiteSpace(item.Id) || item.CheckOut < item.CheckIn)
            {
                return UnprocessableEntity(new
                {
                    title = "A gateway record contains an unknown employee or invalid times."
                });
            }

            bool attendanceExists = await database.Attendance.AnyAsync(
                record => record.EmployeeId == item.EmployeeId && record.Date == item.Date,
                cancellationToken);
            if (attendanceExists)
            {
                return Conflict(new
                {
                    title = "Attendance already exists for an imported day. Resolve it using a correction."
                });
            }

            TimeSpan duration = item.CheckOut is { } end ? end - item.CheckIn : TimeSpan.Zero;
            database.Add(new AttendanceRecord(
                HrmsStore.NewId("ATT"),
                user.Id,
                user.Name,
                item.Date,
                item.CheckIn,
                item.CheckOut,
                item.CheckOut is null ? "Present" : "Checked Out",
                $"{(int)duration.TotalHours}h {duration.Minutes:00}m"));
            database.Add(new FeatureResource
            {
                Area = "biometric",
                Collection = "imported",
                ExternalId = eventKey,
                Payload = "{}"
            });
            imported++;
        }

        JsonObject body = JsonNode.Parse(row.Payload)!.AsObject();
        body["status"] = "Connected";
        body["lastSync"] = DateTimeOffset.UtcNow.ToString("O");
        row.Payload = body.ToJsonString();
        await database.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            imported,
            device = body
        });
    }

    [HttpPost("api/attendance/corrections/{id}/decision")]
    public async Task<IActionResult> CorrectionDecision(
        string id,
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Status is not ("Approved" or "Rejected"))
        {
            return BadRequest();
        }

        FeatureResource? row = await Find("attendance", "corrections", id, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        JsonObject body = JsonNode.Parse(row.Payload)!.AsObject();
        if (body["status"]?.ToString() != "Pending")
        {
            return Conflict();
        }

        string employeeId = body["employeeId"]!.ToString();
        if (!access.CanApprove(employeeId))
        {
            return Forbid();
        }

        if (request.Status == "Approved")
        {
            bool hasValidDate = DateOnly.TryParse(body["date"]?.ToString(), out DateOnly date);
            bool hasValidStart = TimeOnly.TryParse(body["checkIn"]?.ToString(), out TimeOnly start);
            bool hasValidEnd = TimeOnly.TryParse(body["checkOut"]?.ToString(), out TimeOnly end);
            if (!hasValidDate || !hasValidStart || !hasValidEnd || end < start)
            {
                return BadRequest();
            }

            AttendanceRecord[] records = await database.Attendance
                .Where(record => record.EmployeeId == employeeId && record.Date == date)
                .ToArrayAsync(cancellationToken);
            if (records.Length > 1)
            {
                return Conflict(new
                {
                    title = "Multiple attendance sessions require individual review."
                });
            }

            UserAccount? user = await database.Users.FindAsync([employeeId], cancellationToken);
            if (user is null)
            {
                return BadRequest();
            }

            TimeSpan duration = end - start;
            var updated = new AttendanceRecord(
                records.FirstOrDefault()?.Id ?? HrmsStore.NewId("ATT"),
                employeeId,
                user.Name,
                date,
                start,
                end,
                "Checked Out",
                $"{(int)duration.TotalHours}h {duration.Minutes:00}m");
            if (records.Length == 0)
            {
                database.Add(updated);
            }
            else
            {
                database.Entry(records[0]).CurrentValues.SetValues(updated);
            }
        }

        body["status"] = request.Status;
        body["note"] = request.Note;
        body["decidedBy"] = access.UserId;
        row.Payload = body.ToJsonString();
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Ok(body);
    }

    private Task<FeatureResource?> Find(
        string area,
        string collection,
        string id,
        CancellationToken cancellationToken)
    {
        return database.FeatureResources.FirstOrDefaultAsync(
            resource => resource.Area == area &&
                        resource.Collection == collection &&
                        resource.ExternalId == id,
            cancellationToken);
    }
}
