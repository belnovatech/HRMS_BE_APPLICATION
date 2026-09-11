using HRMS.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.Api.Controllers;

public sealed record AttendanceActionRequest(string EmployeeId, string? EmployeeName);

public sealed class AttendanceController(
    HrmsStore store,
    AccessScope access,
    TimeProvider clock,
    IConfiguration configuration) : ApiControllerBase
{
    private DateTime LocalTime
    {
        get
        {
            string timeZoneId = configuration["Application:TimeZone"] ?? "UTC";
            TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTime(clock.GetUtcNow(), timeZone).DateTime;
        }
    }

    [HttpGet]
    public IActionResult List([FromQuery] string? employeeId, [FromQuery] DateOnly? date)
    {
        string[] visibleEmployeeIds = access.VisibleEmployeeIds();
        IQueryable<AttendanceRecord> records = store.Database.Attendance
            .Where(record => access.IsHr || visibleEmployeeIds.Contains(record.EmployeeId))
            .Where(record => employeeId == null || record.EmployeeId == employeeId)
            .Where(record => date == null || record.Date == date);

        return Ok(records);
    }

    [HttpPost("check-in")]
    public IActionResult CheckIn(AttendanceActionRequest request)
    {
        if (!access.CanWriteSelf(request.EmployeeId))
        {
            return Forbid();
        }

        DateTime localTime = LocalTime;
        DateOnly today = DateOnly.FromDateTime(localTime);
        bool isAlreadyCheckedIn = store.Database.Attendance.Any(
            record => record.EmployeeId == request.EmployeeId && record.CheckOut == null);
        if (isAlreadyCheckedIn)
        {
            return Conflict(new ProblemDetails { Title = "Employee is already checked in." });
        }

        UserAccount? account = store.Database.Users.Find(request.EmployeeId);
        if (account is null)
        {
            return BadRequest(new
            {
                title = "Unknown employee."
            });
        }

        string name = account.Name;
        var item = new AttendanceRecord(
            HrmsStore.NewId("ATT"),
            request.EmployeeId,
            name,
            today,
            TimeOnly.FromDateTime(localTime),
            null,
            "Present",
            "0h 00m");

        store.Database.Attendance.Add(item);
        store.Database.SaveChanges();
        return Ok(item);
    }

    [HttpPost("check-out")]
    public IActionResult CheckOut(AttendanceActionRequest request)
    {
        if (!access.CanWriteSelf(request.EmployeeId))
        {
            return Forbid();
        }

        AttendanceRecord? item = store.Database.Attendance
            .Where(record => record.EmployeeId == request.EmployeeId && record.CheckOut == null)
            .OrderByDescending(record => record.Date)
            .FirstOrDefault();
        if (item is null)
        {
            return NotFound(new ProblemDetails { Title = "No open attendance record found." });
        }

        DateTime localTime = LocalTime;
        TimeOnly checkOutTime = TimeOnly.FromDateTime(localTime);
        TimeSpan worked = localTime - item.Date.ToDateTime(item.CheckIn!.Value);
        AttendanceRecord updated = item with
        {
            CheckOut = checkOutTime,
            Status = "Checked Out",
            WorkingHours = $"{(int)worked.TotalHours}h {worked.Minutes:00}m"
        };

        store.Database.Entry(item).CurrentValues.SetValues(updated);
        store.Database.SaveChanges();
        return Ok(updated);
    }
}
