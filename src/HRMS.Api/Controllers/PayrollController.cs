using System.Text.Json;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.Api.Controllers;

public sealed record SalaryStructure(decimal Basic, decimal Allowances, decimal Deductions);

public sealed record PayrollPeriod(int Month, int Year);

public sealed class PayrollController(HrmsStore store, AccessScope access) : ApiControllerBase
{
    public sealed record ProcessPayrollRequest(string[]? PayslipIds);

    [HttpGet("payslips")]
    [HttpGet("history")]
    public IActionResult List()
    {
        IQueryable<Payslip> payslips = VisiblePayslips()
            .OrderByDescending(payslip => payslip.Year)
            .ThenByDescending(payslip => payslip.Month);

        return Ok(payslips);
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        Payslip? payslip = VisiblePayslips().FirstOrDefault(item => item.Id == id);
        return payslip is null ? NotFound() : Ok(payslip);
    }

    [HttpGet("employee/{employeeId}")]
    public IActionResult Employee(string employeeId)
    {
        return Ok(VisiblePayslips().Where(payslip => payslip.EmployeeId == employeeId));
    }

    [HttpGet("employee/monthly")]
    public IActionResult Monthly()
    {
        Payslip? latestPayslip = VisiblePayslips()
            .Where(payslip => payslip.EmployeeId == access.UserId)
            .OrderByDescending(payslip => payslip.Year)
            .ThenByDescending(payslip => payslip.Month)
            .FirstOrDefault();

        return Ok(latestPayslip);
    }

    [Authorize(Roles = "hr")]
    [HttpPut("salary/{employeeId}")]
    public IActionResult SetSalary(string employeeId, SalaryStructure request)
    {
        if (!store.Database.Users.Any(account => account.Id == employeeId))
        {
            return NotFound();
        }

        if (!ValidSalary(request))
        {
            return BadRequest(new
            {
                title = "Salary values must be nonnegative, at most 1 billion, with deductions no greater than gross pay."
            });
        }

        FeatureResource? row = SalaryRow(employeeId);
        if (row is null)
        {
            store.Database.Add(new FeatureResource
            {
                Area = "payroll",
                Collection = "salary",
                ExternalId = employeeId,
                Payload = JsonSerializer.Serialize(request)
            });
        }
        else
        {
            row.Payload = JsonSerializer.Serialize(request);
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        store.Database.SaveChanges();
        return Ok(request);
    }

    [Authorize(Roles = "hr")]
    [HttpGet("salary/{employeeId}")]
    public IActionResult Salary(string employeeId)
    {
        FeatureResource? row = SalaryRow(employeeId);
        return row is null
            ? NotFound()
            : Ok(JsonSerializer.Deserialize<SalaryStructure>(row.Payload));
    }

    [Authorize(Roles = "hr")]
    [HttpPost("calculate/{employeeId}")]
    public IActionResult Calculate(string employeeId, PayrollPeriod period)
    {
        if (!ValidPeriod(period))
        {
            return BadRequest();
        }

        UserAccount? user = store.Database.Users.Find(employeeId);
        if (user is null)
        {
            return NotFound();
        }

        bool payrollExists = store.Database.Payslips.Any(
            payslip => payslip.EmployeeId == employeeId &&
                       payslip.Month == period.Month &&
                       payslip.Year == period.Year);
        if (payrollExists)
        {
            return Conflict(new
            {
                title = "Payroll already exists for this period."
            });
        }

        SalaryStructure? salary = ReadSalary(employeeId);
        if (salary is null)
        {
            return Conflict(new
            {
                title = "Configure salary before calculating payroll."
            });
        }

        Payslip slip = Build(user, salary, period);
        store.Database.Add(slip);
        store.Database.SaveChanges();
        return Ok(slip);
    }
    [Authorize(Roles = "hr")]
    [HttpPost("calculate-all")]
    public IActionResult CalculateAll(PayrollPeriod period)
    {
        if (!ValidPeriod(period))
        {
            return BadRequest();
        }

        UserAccount[] employees = store.Database.Users
            .Where(account => account.Role == "employee")
            .ToArray();
        if (employees.Length == 0)
        {
            return Conflict(new
            {
                title = "No employees are configured."
            });
        }

        var slips = new List<Payslip>();
        foreach (UserAccount employee in employees)
        {
            bool payrollExists = store.Database.Payslips.Any(
                payslip => payslip.EmployeeId == employee.Id &&
                           payslip.Month == period.Month &&
                           payslip.Year == period.Year);
            if (payrollExists)
            {
                return Conflict(new
                {
                    title = "Payroll already exists for one or more employees in this period."
                });
            }

            SalaryStructure? salary = ReadSalary(employee.Id);
            if (salary is null)
            {
                return Conflict(new
                {
                    title = $"Configure salary for {employee.Id}."
                });
            }

            slips.Add(Build(employee, salary, period));
        }

        store.Database.Payslips.AddRange(slips);
        store.Database.SaveChanges();
        return Ok(slips);
    }
    [Authorize(Roles = "hr")]
    [HttpPost("process")]
    public IActionResult Process(ProcessPayrollRequest request)
    {
        if (request.PayslipIds is not { Length: > 0 })
        {
            return BadRequest(new
            {
                title = "Select payslips to process."
            });
        }

        Payslip[] slips = store.Database.Payslips
            .Where(payslip => request.PayslipIds.Contains(payslip.Id))
            .ToArray();
        if (slips.Length != request.PayslipIds.Distinct().Count())
        {
            return NotFound();
        }

        if (slips.Any(payslip => payslip.Status != "Calculated"))
        {
            return Conflict(new
            {
                title = "Only calculated payslips can be processed."
            });
        }

        foreach (Payslip slip in slips)
        {
            store.Database.Entry(slip).CurrentValues.SetValues(slip with
            {
                Status = "Processed"
            });
        }

        store.Database.SaveChanges();
        return Ok(slips.Select(payslip => payslip with { Status = "Processed" }));
    }

    [Authorize(Roles = "hr")]
    [HttpGet("dashboard")]
    public IActionResult Dashboard()
    {
        return Ok(new
        {
            total = store.Database.Payslips.Sum(payslip => payslip.NetPay),
            processed = store.Database.Payslips.Count(payslip => payslip.Status == "Processed"),
            pending = store.Database.Payslips.Count(payslip => payslip.Status != "Processed")
        });
    }

    private IQueryable<Payslip> VisiblePayslips()
    {
        return store.Database.Payslips
            .Where(payslip => access.IsHr || payslip.EmployeeId == access.UserId);
    }

    private FeatureResource? SalaryRow(string employeeId)
    {
        return store.Database.FeatureResources.FirstOrDefault(
            resource => resource.Area == "payroll" &&
                        resource.Collection == "salary" &&
                        resource.ExternalId == employeeId);
    }

    private SalaryStructure? ReadSalary(string employeeId)
    {
        FeatureResource? row = SalaryRow(employeeId);
        return row is null ? null : JsonSerializer.Deserialize<SalaryStructure>(row.Payload);
    }

    public static bool ValidSalary(SalaryStructure salary)
    {
        decimal grossPay = salary.Basic + salary.Allowances;
        return salary.Basic is >= 0 and <= 1_000_000_000 &&
               salary.Allowances is >= 0 and <= 1_000_000_000 &&
               salary.Deductions >= 0 &&
               salary.Deductions <= grossPay;
    }

    public static bool ValidPeriod(PayrollPeriod period)
    {
        return period.Month is >= 1 and <= 12 && period.Year is >= 2000 and <= 2100;
    }

    private static Payslip Build(UserAccount user, SalaryStructure salary, PayrollPeriod period)
    {
        decimal basic = Math.Round(salary.Basic, 2, MidpointRounding.AwayFromZero);
        decimal allowances = Math.Round(salary.Allowances, 2, MidpointRounding.AwayFromZero);
        decimal deductions = Math.Round(salary.Deductions, 2, MidpointRounding.AwayFromZero);

        return new Payslip(
            HrmsStore.NewId("PAY"),
            user.Id,
            user.Name,
            period.Month,
            period.Year,
            basic,
            allowances,
            deductions,
            basic + allowances - deductions,
            "Calculated");
    }
}
