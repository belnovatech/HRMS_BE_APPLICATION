using HRMS.Application.Abstractions;
using HRMS.Application.Abstractions.Persistence;
using HRMS.Application.Common;
using HRMS.Domain.Employees;

namespace HRMS.Application.Features.Employees.CreateEmployee;

public sealed record CreateEmployeeCommand(string EmployeeNumber, string FirstName, string LastName, string Email);
public sealed record CreateEmployeeResponse(Guid Id, string EmployeeNumber);

public sealed class CreateEmployeeHandler(IEmployeeRepository employees, IUnitOfWork unitOfWork)
{
    public async Task<Result<CreateEmployeeResponse>> HandleAsync(
        CreateEmployeeCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.EmployeeNumber) || string.IsNullOrWhiteSpace(command.Email) ||
            string.IsNullOrWhiteSpace(command.FirstName) || string.IsNullOrWhiteSpace(command.LastName) ||
            !System.Net.Mail.MailAddress.TryCreate(command.Email.Trim(), out var address) || address.Address != command.Email.Trim())
        {
            return Result<CreateEmployeeResponse>.Failure(Error.Validation("Employee number and email are required."));
        }

        if (await employees.EmployeeNumberExistsAsync(command.EmployeeNumber.Trim(), cancellationToken))
        {
            return Result<CreateEmployeeResponse>.Failure(Error.Conflict("Employee number already exists."));
        }

        Employee employee = Employee.Create(command.EmployeeNumber, command.FirstName, command.LastName, command.Email);
        await employees.AddAsync(employee, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<CreateEmployeeResponse>.Success(new(employee.Id, employee.EmployeeNumber));
    }
}
