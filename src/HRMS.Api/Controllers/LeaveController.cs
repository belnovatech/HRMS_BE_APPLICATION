using System.Text.Json.Nodes;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Api.Controllers;

public sealed record CreateLeaveRequest(
    string EmployeeId,
    string? EmployeeName,
    string LeaveType,
    DateOnly StartDate,
    DateOnly EndDate,
    string Reason);

public sealed record LeaveDecisionRequest(string Status, string? Reason);

public sealed class LeaveController(HrmsStore store, AccessScope access) : ApiControllerBase
{
    [HttpGet]
    public IActionResult List([FromQuery] string? employeeId, [FromQuery] string? status)
    {
        string[] visibleEmployeeIds = access.VisibleEmployeeIds();
        IQueryable<LeaveRequest> requests = store.Database.Leaves
            .Where(leave => access.IsHr || visibleEmployeeIds.Contains(leave.EmployeeId))
            .Where(leave => employeeId == null || leave.EmployeeId == employeeId)
            .Where(leave => status == null || leave.Status == status)
            .OrderByDescending(leave => leave.AppliedOn);

        return Ok(requests);
    }

    [HttpPost]
    public IActionResult Create(CreateLeaveRequest request)
    {
        using var transaction = store.Database.Database.BeginTransaction(System.Data.IsolationLevel.Serializable);
        if (request.EndDate < request.StartDate)
        {
            return BadRequest(new ProblemDetails { Title = "End date must not precede start date." });
        }

        if (request.EndDate.Year != request.StartDate.Year)
        {
            return BadRequest(new
            {
                title = "Submit separate requests for each calendar year."
            });
        }

        if (!access.CanWriteSelf(request.EmployeeId))
        {
            return Forbid();
        }

        UserAccount? account = store.Database.Users.Find(request.EmployeeId);
        if (account is null)
        {
            return BadRequest(new
            {
                title = "Unknown employee."
            });
        }

        bool datesOverlap = store.Database.Leaves.Any(
            leave => leave.EmployeeId == request.EmployeeId &&
                     leave.Status != "Rejected" &&
                     leave.StartDate <= request.EndDate &&
                     leave.EndDate >= request.StartDate);
        if (datesOverlap)
        {
            return Conflict(new
            {
                title = "Leave dates overlap an existing request."
            });
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.LeaveType))
        {
            return BadRequest();
        }

        string name = account.Name;
        int durationDays = request.EndDate.DayNumber - request.StartDate.DayNumber + 1;
        var leave = new LeaveRequest(
            HrmsStore.NewId("LR"),
            request.EmployeeId,
            name,
            request.LeaveType,
            request.StartDate,
            request.EndDate,
            durationDays,
            request.Reason,
            "Pending",
            DateOnly.FromDateTime(DateTime.UtcNow));

        store.Database.Leaves.Add(leave);
        store.Notify(
            "HR",
            null,
            "Leave",
            "New Leave Request Submitted",
            $"{name} submitted a {request.LeaveType} request.",
            "/hr/leave-management");
        store.Database.SaveChanges();
        transaction.Commit();
        return CreatedAtAction(nameof(List), new
        {
            employeeId = request.EmployeeId
        }, leave);
    }

    [HttpPatch("{id}/decision")]
    public IActionResult Decide(string id, LeaveDecisionRequest request)
    {
        using var transaction = store.Database.Database.BeginTransaction(System.Data.IsolationLevel.Serializable);
        LeaveRequest? leave = store.Database.Leaves.Find(id);
        if (leave is null)
        {
            return NotFound();
        }

        if (!access.CanApprove(leave.EmployeeId))
        {
            return Forbid();
        }

        if (leave.Status != "Pending")
        {
            return Conflict(new
            {
                title = "This request has already been decided."
            });
        }

        if (request.Status is not ("Approved" or "Rejected"))
        {
            return BadRequest(new ProblemDetails { Title = "Status must be Approved or Rejected." });
        }

        if (request.Status == "Approved")
        {
            string balanceId = $"{leave.EmployeeId}-{leave.LeaveType}-{leave.StartDate.Year}";
            FeatureResource? balance = store.Database.FeatureResources.FirstOrDefault(
                resource => resource.Area == "leave" &&
                            resource.Collection == "balances" &&
                            resource.ExternalId == balanceId);
            if (balance is null)
            {
                return Conflict(new
                {
                    title = "Configure the employee's leave entitlement before approval."
                });
            }

            JsonObject body = JsonNode.Parse(balance.Payload)!.AsObject();
            int used = store.Database.Leaves
                .Where(item => item.EmployeeId == leave.EmployeeId &&
                               item.LeaveType == leave.LeaveType &&
                               item.StartDate.Year == leave.StartDate.Year &&
                               item.Status == "Approved")
                .Sum(item => item.DurationDays);
            if (used + leave.DurationDays > body["total"]!.GetValue<int>())
            {
                return Conflict(new
                {
                    title = "Insufficient leave balance."
                });
            }

            body["used"] = used + leave.DurationDays;
            balance.Payload = body.ToJsonString();
        }
        LeaveRequest updated = leave with
        {
            Status = request.Status,
            RejectReason = request.Reason
        };
        store.Database.Entry(leave).CurrentValues.SetValues(updated);
        store.Notify(
            "Employee",
            leave.EmployeeId,
            "Leave",
            $"Leave Request {request.Status}",
            $"Your {leave.LeaveType} request has been {request.Status.ToLowerInvariant()}.",
            "/employee/leave");
        store.Database.SaveChanges();
        transaction.Commit();
        return Ok(updated);
    }
}
