using System.ComponentModel.DataAnnotations;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Api.Controllers;

public sealed record CreateAccountRequest(
    [EmailAddress] string Email,
    string Username,
    string Password,
    string Role,
    string Name,
    string Department,
    string Designation,
    string? ReportsTo,
    string? EmployeeNumber = null);

[Authorize(Roles = "hr")]
public sealed class AccountsController(HrmsDbContext database) : ApiControllerBase
{
    [HttpGet]
    public IActionResult List()
    {
        var accounts = database.Users
            .AsNoTracking()
            .AsEnumerable()
            .Select(AuthController.PublicUser);

        return Ok(accounts);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        CreateAccountRequest request,
        CancellationToken cancellationToken)
    {
        bool hasValidRole = request.Role is "employee" or "manager" or "hr";
        if (!hasValidRole ||
            !PasswordSecurity.Strong(request.Password) ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest();
        }

        bool managerExists = request.ReportsTo is null || await database.Users.AnyAsync(
            account => account.Id == request.ReportsTo && account.Role == "manager",
            cancellationToken);
        if (!managerExists)
        {
            return BadRequest(new
            {
                title = "ReportsTo must identify a manager account."
            });
        }

        string address = request.Email.Trim().ToLowerInvariant();
        string username = request.Username.Trim().ToLowerInvariant();
        bool accountExists = await database.Users.AnyAsync(
            account => account.Email.ToLower() == address || account.Username.ToLower() == username,
            cancellationToken);
        if (accountExists)
        {
            return Conflict();
        }

        string id = request.EmployeeNumber?.Trim().ToUpperInvariant() ?? NewAccountId(request.Role);
        if (await database.Users.AnyAsync(account => account.Id == id, cancellationToken))
        {
            return Conflict();
        }

        if (request.Role == "employee")
        {
            var employee = await database.Employees.FirstOrDefaultAsync(
                employee => employee.EmployeeNumber == id || employee.Email == address,
                cancellationToken);
            if (employee is not null && (employee.EmployeeNumber != id || employee.Email != address))
            {
                return Conflict(new
                {
                    title = "Use the existing employee number and email."
                });
            }

            if (employee is null)
            {
                string[] names = request.Name.Trim().Split(' ', 2);
                string lastName = names.Length > 1 ? names[1] : names[0];
                database.Employees.Add(
                    HRMS.Domain.Employees.Employee.Create(id, names[0], lastName, address));
            }
        }

        var user = new UserAccount(
            id,
            address,
            username,
            "",
            request.Role,
            request.Name,
            request.Designation,
            request.Department,
            id,
            request.ReportsTo);
        database.Users.Add(user with
        {
            Password = PasswordSecurity.Hasher.HashPassword(user, request.Password)
        });

        await database.SaveChangesAsync(cancellationToken);
        return Created($"/api/accounts/{id}", AuthController.PublicUser(user));
    }

    [HttpPut("{id}/manager")]
    public async Task<IActionResult> AssignManager(
        string id,
        AssignManagerRequest request,
        CancellationToken cancellationToken)
    {
        UserAccount? user = await database.Users.FindAsync([id], cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        bool managerExists = request.ManagerId is null || await database.Users.AnyAsync(
            account => account.Id == request.ManagerId && account.Role == "manager",
            cancellationToken);
        if (request.ManagerId == id || !managerExists)
        {
            return BadRequest();
        }

        database.Entry(user).CurrentValues.SetValues(user with
        {
            ReportsTo = request.ManagerId
        });

        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string NewAccountId(string role)
    {
        string prefix = role switch
        {
            "employee" => "EMP",
            "manager" => "MGR",
            _ => "HR"
        };

        return HrmsStore.NewId(prefix);
    }

    public sealed record AssignManagerRequest(string? ManagerId);
}
