using HRMS.Domain.Employees;
namespace HRMS.Domain.UnitTests;
public class EmployeeTests
{
    [Fact]
    public void Creation_normalizes_identity_and_records_creation_time()
    {
        var employee = Employee.Create(" emp100 ", " Ada ", " Lovelace ", " ADA@example.com ");
        Assert.Equal("EMP100", employee.EmployeeNumber);
        Assert.Equal("Ada", employee.FirstName);
        Assert.Equal("ada@example.com", employee.Email);
        Assert.NotEqual(default, employee.CreatedAtUtc);
        Assert.Equal(EmploymentStatus.Active, employee.Status);
    }
    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("Name <name@example.com>")]
    public void Invalid_email_is_rejected(string email) => Assert.ThrowsAny<ArgumentException>(() => Employee.Create("EMP", "Ada", "Lovelace", email));
    [Fact]
    public void Undefined_status_is_rejected()
    {
        var employee = Employee.Create("EMP", "Ada", "Lovelace", "ada@example.com");
        Assert.Throws<ArgumentException>(() => employee.Update("Ada", "Lovelace", "ada@example.com", (EmploymentStatus)99));
    }
}
