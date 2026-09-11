using HRMS.Api.Services;
using HRMS.Application.Abstractions.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.Api.Controllers;

[Authorize(Roles = "hr")]
public sealed class DashboardController(IEmployeeRepository employees, HrmsStore store) : ApiControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        var records = await employees.ListAsync(cancellationToken);
        int total = records.Count;
        DateTime now = DateTime.UtcNow;
        return Ok(new
        {
            total_employees = new
            {
                count = total
            },
            active_employees = new
            {
                count = records.Count(x => x.Status == HRMS.Domain.Employees.EmploymentStatus.Active)
            },
            inactive_employees = new
            {
                count = records.Count(x => x.Status != HRMS.Domain.Employees.EmploymentStatus.Active)
            },
            uninformed_leaves = new
            {
                count = store.Database.Attendance.Count(x => x.Status == "Absent")
            },
            pending_approvals = new
            {
                count = store.Database.Leaves.Count(x => x.Status == "Pending")
            },
            monthly_payroll = new
            {
                amount = store.Database.Payslips.Where(x => x.Month == now.Month && x.Year == now.Year).AsEnumerable().Sum(x => x.NetPay)
            }
        });
    }
}
