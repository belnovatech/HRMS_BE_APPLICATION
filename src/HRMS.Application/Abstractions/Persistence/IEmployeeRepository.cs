using HRMS.Domain.Employees;

namespace HRMS.Application.Abstractions.Persistence;

public interface IEmployeeRepository
{
    Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Employee>> ListAsync(CancellationToken cancellationToken = default);
    Task<bool> EmployeeNumberExistsAsync(string employeeNumber, CancellationToken cancellationToken = default);
    Task AddAsync(Employee employee, CancellationToken cancellationToken = default);
}
