using HRMS.Domain.Employees;
using HRMS.Application.Features.Employees.CreateEmployee;
namespace HRMS.ArchitectureTests;
public class DependencyTests
{
    [Fact]
    public void Domain_has_no_outward_project_dependencies() => Assert.DoesNotContain(typeof(Employee).Assembly.GetReferencedAssemblies(), x => x.Name!.StartsWith("HRMS."));
    [Fact]
    public void Application_does_not_reference_api_or_infrastructure() => Assert.DoesNotContain(typeof(CreateEmployeeHandler).Assembly.GetReferencedAssemblies(), x => x.Name is "HRMS.Api" or "HRMS.Infrastructure");
}
