using HRMS.Application.Abstractions;
using HRMS.Application.Abstractions.Persistence;
using HRMS.Application.Features.Employees.CreateEmployee;
using HRMS.Domain.Employees;
namespace HRMS.Application.UnitTests;
public class EmployeeUseCaseTests
{
    [Fact]
    public async Task Duplicate_employee_does_not_write()
    {
        var repository = new Repository { Exists = true };
        var handler = new CreateEmployeeHandler(repository, repository);
        var result = await handler.HandleAsync(new("EMP1", "Ada", "Lovelace", "ada@example.com"));
        Assert.True(result.IsFailure);
        Assert.Equal(0, repository.Writes);
    }
    [Fact]
    public async Task Invalid_names_do_not_write()
    {
        var repository = new Repository();
        var handler = new CreateEmployeeHandler(repository, repository);
        Assert.True((await handler.HandleAsync(new("EMP1", "", "Lovelace", "ada@example.com"))).IsFailure);
        Assert.Equal(0, repository.Writes);
    }
    [Fact]
    public async Task Valid_employee_is_persisted_once()
    {
        var repository = new Repository();
        var handler = new CreateEmployeeHandler(repository, repository);
        var result = await handler.HandleAsync(new("EMP1", "Ada", "Lovelace", "ada@example.com"));
        Assert.False(result.IsFailure);
        Assert.Equal(1, repository.Writes);
        Assert.Equal(result.Value.Id, repository.Employee!.Id);
    }
    private sealed class Repository : IEmployeeRepository, IUnitOfWork
    {
        public bool Exists; public int Writes; public Employee? Employee;
        public Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Employee);
        public Task<IReadOnlyCollection<Employee>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Employee>>([]);
        public Task<bool> EmployeeNumberExistsAsync(string number, CancellationToken cancellationToken = default) => Task.FromResult(Exists);
        public Task AddAsync(Employee employee, CancellationToken cancellationToken = default)
        {
            Employee = employee;
            return Task.CompletedTask;
        }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Writes++;
            return Task.FromResult(1);
        }
    }
}
