using System.Collections.Concurrent;
using HRMS.Application.Abstractions;
using HRMS.Application.Abstractions.Persistence;
using HRMS.Domain.Employees;

namespace HRMS.Infrastructure.Persistence;

public sealed class InMemoryEmployeeRepository : IEmployeeRepository, IUnitOfWork
{
    private readonly ConcurrentDictionary<Guid, Employee> _employees = new();

    public Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _employees.TryGetValue(id, out Employee? employee);
        return Task.FromResult(employee);
    }

    public Task<IReadOnlyCollection<Employee>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Employee>>(_employees.Values.OrderBy(x => x.EmployeeNumber).ToArray());

    public Task<bool> EmployeeNumberExistsAsync(string employeeNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult(_employees.Values.Any(x =>
            string.Equals(x.EmployeeNumber, employeeNumber, StringComparison.OrdinalIgnoreCase)));

    public Task AddAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        if (!_employees.TryAdd(employee.Id, employee))
        {
            throw new InvalidOperationException($"Employee '{employee.Id}' already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}
