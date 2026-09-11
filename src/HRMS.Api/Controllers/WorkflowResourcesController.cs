using System.Text.Json;
using System.Text.Json.Nodes;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Api.Controllers;

[ApiController]
public sealed class WorkflowResourcesController(HrmsDbContext database, AccessScope access) : ControllerBase
{
    [HttpGet("api/leave/policies")]
    [HttpGet("api/leave/balances")]
    [HttpGet("api/attendance/corrections")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var (area, collection) = Keys();
        FeatureResource[] resources = await database.FeatureResources
            .AsNoTracking()
            .Where(resource => resource.Area == area && resource.Collection == collection)
            .ToArrayAsync(cancellationToken);
        JsonObject[] bodies = resources
            .Select(resource => JsonNode.Parse(resource.Payload)!.AsObject())
            .Where(body => collection == "policies" ||
                           access.IsHr ||
                           access.CanRead(body["employeeId"]?.ToString() ?? ""))
            .ToArray();

        if (collection == "balances")
        {
            foreach (JsonObject body in bodies)
            {
                string employeeId = body["employeeId"]!.ToString();
                string leaveType = body["leaveType"]!.ToString();
                int year = body["year"]!.GetValue<int>();
                int used = database.Leaves
                    .Where(leave => leave.EmployeeId == employeeId &&
                                    leave.LeaveType == leaveType &&
                                    leave.Status == "Approved" &&
                                    leave.StartDate.Year == year)
                    .Sum(leave => leave.DurationDays);
                body["used"] = used;
                body["available"] = body["total"]!.GetValue<int>() - used;
            }
        }

        return Ok(bodies);
    }

    [HttpPost("api/leave/policies")]
    [HttpPost("api/leave/balances")]
    [HttpPost("api/attendance/corrections")]
    public async Task<IActionResult> Create(
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        var (area, collection) = Keys();
        JsonObject body = Parse(payload);
        if (collection != "corrections" && !access.IsHr)
        {
            return Forbid();
        }

        if (collection == "corrections")
        {
            body["employeeId"] = access.UserId;
            body["status"] = "Pending";
            bool hasValidDate = DateOnly.TryParse(body["date"]?.ToString(), out _);
            bool hasValidCheckIn = TimeOnly.TryParse(body["checkIn"]?.ToString(), out _);
            bool hasValidCheckOut = TimeOnly.TryParse(body["checkOut"]?.ToString(), out _);
            if (!hasValidDate || !hasValidCheckIn || !hasValidCheckOut)
            {
                return BadRequest();
            }
        }

        if (collection == "balances" && !ValidBalance(body))
        {
            return BadRequest();
        }

        string id = collection == "balances"
            ? $"{body["employeeId"]}-{body["leaveType"]}-{body["year"]}"
            : Guid.NewGuid().ToString("N");
        body["id"] = id;
        database.Add(new FeatureResource
        {
            Area = area,
            Collection = collection,
            ExternalId = id,
            Payload = body.ToJsonString()
        });

        if (collection == "corrections")
        {
            var store = new HrmsStore(database);
            store.Notify(
                "HR",
                null,
                "Attendance",
                "Attendance correction requested",
                $"{access.UserId} requested an attendance correction.",
                "/hr/attendance");
        }

        await database.SaveChangesAsync(cancellationToken);
        return Created($"{Request.Path}/{id}", body);
    }

    [Authorize(Roles = "hr")]
    [HttpPatch("api/leave/policies/{id}")]
    [HttpPatch("api/leave/balances/{id}")]
    public async Task<IActionResult> Patch(
        string id,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        var (area, collection) = Keys();
        FeatureResource? row = await FindResource(area, collection, id, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        JsonObject body = JsonNode.Parse(row.Payload)!.AsObject();
        foreach ((string key, JsonNode? value) in Parse(payload))
        {
            if (key is not ("id" or "employeeId" or "leaveType" or "year" or "used"))
            {
                body[key] = value?.DeepClone();
            }
        }

        if (collection == "balances" && !ValidBalance(body))
        {
            return BadRequest();
        }

        row.Payload = body.ToJsonString();
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Ok(body);
    }

    [Authorize(Roles = "hr")]
    [HttpDelete("api/leave/policies/{id}")]
    [HttpDelete("api/leave/balances/{id}")]
    [HttpDelete("api/attendance/corrections/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var (area, collection) = Keys();
        FeatureResource? row = await FindResource(area, collection, id, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        database.Remove(row);
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private bool ValidBalance(JsonObject body)
    {
        bool hasEmployee = body["employeeId"] is JsonValue &&
                           database.Users.Any(account => account.Id == body["employeeId"]!.ToString());
        bool hasLeaveType = !string.IsNullOrWhiteSpace(body["leaveType"]?.ToString());
        bool hasValidYear = int.TryParse(body["year"]?.ToString(), out int year) &&
                            year is >= 2000 and <= 2100;
        bool hasValidTotal = int.TryParse(body["total"]?.ToString(), out int total) &&
                             total is >= 0 and <= 366;

        return hasEmployee && hasLeaveType && hasValidYear && hasValidTotal;
    }

    private (string, string) Keys()
    {
        string[] pathSegments = Request.Path.Value!.Trim('/').Split('/');
        return (pathSegments[1], pathSegments[2]);
    }

    private Task<FeatureResource?> FindResource(
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

    private static JsonObject Parse(JsonElement value)
    {
        return JsonNode.Parse(value.GetRawText()) as JsonObject
            ?? throw new BadHttpRequestException("A JSON object is required.");
    }
}
