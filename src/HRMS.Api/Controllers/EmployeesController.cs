using HRMS.Application.Abstractions.Persistence;
using HRMS.Application.Features.Employees.CreateEmployee;
using HRMS.Domain.Employees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.Api.Controllers;

public sealed record UpdateEmployeeRequest(string FirstName, string LastName, string Email, EmploymentStatus Status);

[Authorize(Roles = "hr")]
public sealed class EmployeesController(IEmployeeRepository employees, HRMS.Api.Services.HrmsDbContext database) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<Employee>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<Employee>>> List(CancellationToken cancellationToken) =>
        Ok(await employees.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<Employee>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Employee>> Get(Guid id, CancellationToken cancellationToken)
    {
        Employee? employee = await employees.GetByIdAsync(id, cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }

    [HttpPost]
    [ProducesResponseType<CreateEmployeeResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        CreateEmployeeCommand command,
        [FromServices] CreateEmployeeHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(command, cancellationToken);
        return result.IsFailure
            ? FromFailure(result)
            : CreatedAtAction(nameof(Get), new
            {
                id = result.Value.Id
            }, result.Value);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateEmployeeRequest request, CancellationToken cancellationToken)
    {
        Employee? employee = await employees.GetByIdAsync(id, cancellationToken);
        if (employee is null)
        {
            return NotFound();
        }

        employee.Update(request.FirstName, request.LastName, request.Email, request.Status);
        employee.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Ok(employee);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        Employee? employee = await employees.GetByIdAsync(id, cancellationToken);
        if (employee is null)
        {
            return NotFound();
        }

        employee.Update(employee.FirstName, employee.LastName, employee.Email, EmploymentStatus.Inactive);
        employee.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
