using HRMS.Application.Abstractions;
using HRMS.Application.Abstractions.Persistence;
using HRMS.Domain.Employees;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Api.Services;

public sealed class EfEmployeeRepository(HrmsDbContext database) : IEmployeeRepository, IUnitOfWork
{
    public Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Employees.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyCollection<Employee>> ListAsync(CancellationToken cancellationToken = default) =>
        await database.Employees.OrderBy(x => x.EmployeeNumber).ToArrayAsync(cancellationToken);

    public Task<bool> EmployeeNumberExistsAsync(string employeeNumber, CancellationToken cancellationToken = default) =>
        database.Employees.AnyAsync(x => x.EmployeeNumber.ToLower() == employeeNumber.ToLower(), cancellationToken);

    public async Task AddAsync(Employee employee, CancellationToken cancellationToken = default) =>
        await database.Employees.AddAsync(employee, cancellationToken);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
