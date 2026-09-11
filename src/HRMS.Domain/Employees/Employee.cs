using HRMS.Domain.Common;

namespace HRMS.Domain.Employees;

public sealed class Employee : Entity, IAuditableEntity
{
    private Employee(Guid id, string employeeNumber, string firstName, string lastName, string email)
        : base(id)
    {
        EmployeeNumber = employeeNumber;
        FirstName = firstName;
        LastName = lastName;
        Email = email;
    }

    public string EmployeeNumber
    {
        get; private set;
    }
    public string FirstName
    {
        get; private set;
    }
    public string LastName
    {
        get; private set;
    }
    public string Email
    {
        get; private set;
    }
    public EmploymentStatus Status { get; private set; } = EmploymentStatus.Active;
    public Guid? DepartmentId
    {
        get; private set;
    }
    public Guid? ManagerId
    {
        get; private set;
    }
    public DateTimeOffset CreatedAtUtc
    {
        get; set;
    }
    public Guid? CreatedBy
    {
        get; set;
    }
    public DateTimeOffset? UpdatedAtUtc
    {
        get; set;
    }
    public Guid? UpdatedBy
    {
        get; set;
    }

    public static Employee Create(string employeeNumber, string firstName, string lastName, string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (!System.Net.Mail.MailAddress.TryCreate(email.Trim(), out var address) || address.Address != email.Trim())
            throw new ArgumentException("A valid email address is required.", nameof(email));

        return new Employee(
            Guid.NewGuid(),
            employeeNumber.Trim().ToUpperInvariant(),
            firstName.Trim(),
            lastName.Trim(),
            email.Trim().ToLowerInvariant())
        {
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public void Update(string firstName, string lastName, string email, EmploymentStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (!System.Net.Mail.MailAddress.TryCreate(email.Trim(), out var address) || address.Address != email.Trim())
            throw new ArgumentException("A valid email address is required.", nameof(email));
        if (!Enum.IsDefined(status))
            throw new ArgumentException("Invalid employment status.", nameof(status));
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        Email = email.Trim().ToLowerInvariant();
        Status = status;
    }
}
