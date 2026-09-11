using HRMS.Domain.Employees;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Api.Services;

public sealed class HrmsDbContext(
    DbContextOptions<HrmsDbContext> options,
    IHttpContextAccessor? http = null) : DbContext(options)
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AddAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AddAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void AddAudit()
    {
        if (http?.HttpContext?.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var changes = ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(entry => entry.Entity is not FeatureResource resource ||
                            resource.Area is not ("auth" or "audit" or "notificationReads"))
            .Select(entry => new
            {
                entity = entry.Metadata.ClrType.Name,
                operation = entry.State.ToString(),
                id = entry.Properties
                    .FirstOrDefault(property => property.Metadata.IsPrimaryKey())?
                    .CurrentValue?
                    .ToString()
            })
            .ToArray();
        if (changes.Length == 0)
        {
            return;
        }

        string? actorId = http.HttpContext.User
            .FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?
            .Value;
        var auditEvent = new
        {
            actor = actorId,
            method = http.HttpContext.Request.Method,
            path = http.HttpContext.Request.Path.Value,
            at = DateTimeOffset.UtcNow,
            changes
        };
        FeatureResources.Add(new FeatureResource
        {
            Area = "audit",
            Collection = "events",
            ExternalId = Guid.NewGuid().ToString("N"),
            Payload = System.Text.Json.JsonSerializer.Serialize(auditEvent)
        });
    }

    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<LeaveRequest> Leaves => Set<LeaveRequest>();
    public DbSet<AttendanceRecord> Attendance => Set<AttendanceRecord>();
    public DbSet<HelpTicket> Tickets => Set<HelpTicket>();
    public DbSet<EmployeeDocument> Documents => Set<EmployeeDocument>();
    public DbSet<NotificationItem> Notifications => Set<NotificationItem>();
    public DbSet<Payslip> Payslips => Set<Payslip>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<FeatureResource> FeatureResources => Set<FeatureResource>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAccount>().HasKey(x => x.Id);
        modelBuilder.Entity<UserAccount>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<UserAccount>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<LeaveRequest>().HasKey(x => x.Id);
        modelBuilder.Entity<AttendanceRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<AttendanceRecord>()
            .HasIndex(record => record.EmployeeId)
            .IsUnique()
            .HasFilter("\"CheckOut\" IS NULL");
        modelBuilder.Entity<AttendanceRecord>().Property(x => x.CheckOut).IsConcurrencyToken();
        modelBuilder.Entity<HelpTicket>().HasKey(x => x.Id);
        modelBuilder.Entity<EmployeeDocument>().HasKey(x => x.Id);
        modelBuilder.Entity<NotificationItem>().HasKey(x => x.Id);
        modelBuilder.Entity<Payslip>().HasKey(x => x.Id);
        modelBuilder.Entity<Payslip>()
            .HasIndex(payslip => new { payslip.EmployeeId, payslip.Month, payslip.Year })
            .IsUnique();
        modelBuilder.Entity<Payslip>().Property(x => x.Status).IsConcurrencyToken();
        modelBuilder.Entity<LeaveRequest>().Property(x => x.Status).IsConcurrencyToken();
        modelBuilder.Entity<Employee>().HasKey(x => x.Id);
        modelBuilder.Entity<Employee>().HasIndex(x => x.EmployeeNumber).IsUnique();
        modelBuilder.Entity<Employee>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<Employee>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<FeatureResource>().HasKey(x => x.Id);
        modelBuilder.Entity<FeatureResource>()
            .HasIndex(resource => new { resource.Area, resource.Collection, resource.ExternalId })
            .IsUnique();
        modelBuilder.Entity<FeatureResource>().Property(x => x.Payload).HasColumnType("jsonb");
        modelBuilder.Entity<FeatureResource>().Property(x => x.Payload).IsConcurrencyToken();
        modelBuilder.Entity<Payslip>().Property(x => x.Basic).HasPrecision(18, 2);
        modelBuilder.Entity<Payslip>().Property(x => x.Allowances).HasPrecision(18, 2);
        modelBuilder.Entity<Payslip>().Property(x => x.Deductions).HasPrecision(18, 2);
        modelBuilder.Entity<Payslip>().Property(x => x.NetPay).HasPrecision(18, 2);

        modelBuilder.Entity<UserAccount>().HasData(
            new UserAccount(
                "HR001", "admin@hr.com", "admin_hr", "password123", "hr",
                "Priya Sharma", "HR Director", "Human Resources"),
            new UserAccount(
                "MGR001", "manager@belnova.com", "vikram_mgr", "password123", "manager",
                "Vikramaditya Rao", "Engineering Manager", "Engineering"),
            new UserAccount(
                "EMP001", "arjun@belnova.com", "arjun_mehta", "password123", "employee",
                "Arjun Mehta", "Senior Engineer", "Engineering", "EMP001", "Vikramaditya Rao"),
            new UserAccount(
                "EMP002", "kavya@belnova.com", "kavya_nair", "password123", "employee",
                "Kavya Nair", "UI/UX Designer", "Design", "EMP002", "Vikramaditya Rao"));
    }
}

public sealed class FeatureResource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Area { get; set; }
    public required string Collection { get; set; }
    public required string ExternalId { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
