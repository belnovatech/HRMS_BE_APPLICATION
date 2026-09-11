using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HRMS.Api.Services;

public static class DatabaseInitialization
{
    public static async Task SecureAccounts(
        HrmsDbContext database,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        foreach (UserAccount user in await database.Users.ToArrayAsync())
        {
            string password = user.Password;
            bool hashed = password.StartsWith("AQAAAA", StringComparison.Ordinal);
            bool usesDefaultPassword = password == "password123" ||
                                       (hashed && PasswordSecurity.Verify(user, "password123"));
            if (!environment.IsDevelopment() && usesDefaultPassword)
            {
                password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                bool alreadyDisabled = await database.FeatureResources.AnyAsync(
                    resource => resource.Area == "auth" &&
                                resource.Collection == "disabled" &&
                                resource.ExternalId == user.Id);
                if (!alreadyDisabled)
                {
                    database.Add(new FeatureResource
                    {
                        Area = "auth",
                        Collection = "disabled",
                        ExternalId = user.Id,
                        Payload = "{}"
                    });
                }
            }

            if (!hashed || password != user.Password)
            {
                database.Entry(user).CurrentValues.SetValues(user with
                {
                    Password = PasswordSecurity.Hasher.HashPassword(user, password)
                });
            }

            if (user.Role == "employee")
            {
                bool employeeExists = await database.Employees.AnyAsync(
                    employee => employee.EmployeeNumber == user.Id || employee.Email == user.Email);
                if (!employeeExists)
                {
                    string[] names = user.Name.Split(' ', 2);
                    string lastName = names.Length > 1 ? names[1] : names[0];
                    database.Employees.Add(
                        HRMS.Domain.Employees.Employee.Create(user.Id, names[0], lastName, user.Email));
                }
            }

            if (!string.IsNullOrEmpty(user.ReportsTo))
            {
                UserAccount[] managers = await database.Users
                    .Where(account => account.Role == "manager" && account.Name == user.ReportsTo)
                    .ToArrayAsync();
                if (managers.Length == 1)
                {
                    database.Entry(user).Property(x => x.ReportsTo).CurrentValue = managers[0].Id;
                }
            }
        }

        if (configuration["Bootstrap:Email"] is { Length: > 0 } address &&
            !await database.Users.AnyAsync(account => account.Email == address))
        {
            string password = configuration["Bootstrap:Password"] ?? "";
            if (!PasswordSecurity.Strong(password))
            {
                throw new InvalidOperationException("Bootstrap password must contain 12-128 characters, letters and digits.");
            }

            var user = new UserAccount(
                HrmsStore.NewId("HR"),
                address,
                address,
                "",
                "hr",
                "HR Administrator",
                "Administrator",
                "Human Resources");
            database.Users.Add(user with
            {
                Password = PasswordSecurity.Hasher.HashPassword(user, password)
            });
        }

        await database.SaveChangesAsync();
    }
}

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = scopes.CreateScope();
        HrmsDbContext database = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();
        bool connected = await database.Database.CanConnectAsync(cancellationToken);
        return connected
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database unavailable.");
    }
}
