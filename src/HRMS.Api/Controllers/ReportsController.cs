using HRMS.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Api.Controllers;

[Authorize(Roles = "hr")]
public sealed class ReportsController(HrmsDbContext database) : ApiControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) => Ok(new
    {
        employees = await database.Employees.CountAsync(cancellationToken),
        users = await database.Users.CountAsync(cancellationToken),
        pendingLeaves = await database.Leaves.CountAsync(x => x.Status == "Pending", cancellationToken),
        attendanceRecords = await database.Attendance.CountAsync(cancellationToken),
        payrollTotal = await database.Payslips.SumAsync(x => x.NetPay, cancellationToken),
        openTickets = await database.Tickets.CountAsync(x => x.Status == "Open", cancellationToken)
    });

    [HttpGet("attendance")]
    public IActionResult Attendance() => Ok(database.Attendance.AsNoTracking());
    [HttpGet("leave")]
    public IActionResult Leave() => Ok(database.Leaves.AsNoTracking());
    [HttpGet("payroll")]
    public IActionResult Payroll() => Ok(database.Payslips.AsNoTracking());
    [HttpGet("employees")]
    public IActionResult Employees() => Ok(database.Employees.AsNoTracking());
}
