using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace HRMS.Api.IntegrationTests;
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly bool postgres = Environment.GetEnvironmentVariable("HRMS_TEST_POSTGRES") == "1";
    private string? postgresConnection;
    private string? adminConnection;
    private readonly string databaseName = "hrms_test_" + Guid.NewGuid().ToString("N");
    public readonly TestEmail Email = new();
    public readonly TestClock Clock = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (postgres)
        {
            IConfiguration config = new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).AddEnvironmentVariables().Build();
            var template = new NpgsqlConnectionStringBuilder(config["ConnectionStrings:Database"] ?? "Host=localhost;Database=hrms_local;Username=postgres");
            template.Database = "postgres";
            adminConnection = template.ConnectionString;
            using var admin = new NpgsqlConnection(adminConnection);
            admin.Open();
            using var command = new NpgsqlCommand("CREATE DATABASE " + databaseName, admin);
            command.ExecuteNonQuery();
            template.Database = databaseName;
            postgresConnection = template.ConnectionString;
        }
        else
            connection.Open();
        builder.UseEnvironment("Testing");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("ConnectionStrings:Database", "Host=unused");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<HrmsDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<HrmsDbContext>>();
            services.AddDbContext<HrmsDbContext>(options => { if (postgres) options.UseNpgsql(postgresConnection); else options.UseSqlite(connection); });
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
    public HttpClient Start()
    {
        HttpClient client = CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost")
        });
        using IServiceScope scope = Services.CreateScope();
        HrmsDbContext db = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();
        if (postgres)
            db.Database.Migrate();
        else
            db.Database.EnsureCreated();
        foreach (UserAccount user in db.Users.ToArray())
            db.Entry(user).CurrentValues.SetValues(user with
            {
                Password = PasswordSecurity.Hasher.HashPassword(user, "SecureTest123!"),
                ReportsTo = user.Id == "EMP001" ? "MGR001" : null
            });
        db.Add(new FeatureResource { Area = "leave", Collection = "balances", ExternalId = "EMP001-Casual-2026", Payload = "{\"employeeId\":\"EMP001\",\"leaveType\":\"Casual\",\"year\":2026,\"total\":12}" });
        db.SaveChanges();
        return client;
    }
    public static async Task Login(HttpClient client, string id)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            identifier = id,
            password = "SecureTest123!"
        });
        response.EnsureSuccessStatusCode();
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            connection.Dispose();
            if (postgres && adminConnection is not null)
            {
                // Only this factory's randomly generated test database is ever dropped.
                if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^hrms_test_[0-9a-f]{32}$"))
                    throw new InvalidOperationException();
                NpgsqlConnection.ClearAllPools();
                using var admin = new NpgsqlConnection(adminConnection);
                admin.Open();
                using var command = new NpgsqlCommand("DROP DATABASE IF EXISTS " + databaseName + " WITH (FORCE)", admin);
                command.ExecuteNonQuery();
            }
        }
    }
}
public sealed class TestEmail : IEmailSender
{
    public string? Code
    {
        get; private set;
    }
    public Task SendOtp(string address, string code, CancellationToken ct)
    {
        Code = code;
        return Task.CompletedTask;
    }
}
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
public partial class ApiTests
{
    [Theory]
    [InlineData("/api/employees")]
    [InlineData("/api/payroll/payslips")]
    [InlineData("/api/settings/security")]
    [InlineData("/api/files/anything")]
    public async Task Anonymous_requests_are_rejected(string route)
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
    }
    [Fact]
    public async Task Credentials_identity_expiry_and_logout_are_enforced()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new
        {
            identifier = "EMP001",
            password = "wrong"
        })).StatusCode);
        await ApiFactory.Login(client, "EMP001");
        client.DefaultRequestHeaders.Add("X-Employee-Id", "HR001");
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("EMP001", me.GetProperty("id").GetString());
        Assert.False(me.TryGetProperty("password", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/accounts")).StatusCode);
        (await client.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        await ApiFactory.Login(client, "EMP001");
        factory.Clock.Now = factory.Clock.Now.AddHours(9);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
    [Fact]
    public async Task Otp_is_private_single_use_and_password_reset_revokes_sessions()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        await ApiFactory.Login(client, "EMP001");
        var sent = await client.PostAsJsonAsync("/api/auth/request-otp", new
        {
            identifier = "EMP001"
        });
        sent.EnsureSuccessStatusCode();
        Assert.DoesNotContain(factory.Email.Code!, await sent.Content.ReadAsStringAsync());
        var reset = new
        {
            identifier = "EMP001",
            otp = factory.Email.Code,
            newPassword = "ChangedPass123!"
        };
        (await client.PostAsJsonAsync("/api/auth/reset-password", reset)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", reset)).StatusCode);
        (await client.PostAsJsonAsync("/api/auth/login", new
        {
            identifier = "EMP001",
            password = "ChangedPass123!"
        })).EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task Otp_locks_after_five_failures()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        (await client.PostAsJsonAsync("/api/auth/request-otp", new
        {
            identifier = "EMP001"
        })).EnsureSuccessStatusCode();
        for (int i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/verify-otp", new
            {
                identifier = "EMP001",
                otp = "000000"
            })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/verify-otp", new
        {
            identifier = "EMP001",
            otp = factory.Email.Code
        })).StatusCode);
    }
    [Fact]
    public async Task Leave_cannot_be_spoofed_overlapped_or_approved_twice()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        await ApiFactory.Login(client, "EMP001");
        object Leave(string id) => new
        {
            employeeId = id,
            leaveType = "Casual",
            startDate = "2026-10-01",
            endDate = "2026-10-02",
            reason = "Personal"
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/leave", Leave("EMP002"))).StatusCode);
        var response = await client.PostAsJsonAsync("/api/leave", Leave("EMP001"));
        response.EnsureSuccessStatusCode();
        string id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/leave", Leave("EMP001"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/leave/{id}/decision", new
        {
            status = "Approved"
        })).StatusCode);
        await ApiFactory.Login(client, "EMP002");
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/leave")).EnumerateArray());
        await ApiFactory.Login(client, "MGR001");
        (await client.PatchAsJsonAsync($"/api/leave/{id}/decision", new
        {
            status = "Approved"
        })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"/api/leave/{id}/decision", new
        {
            status = "Approved"
        })).StatusCode);
    }
    [Fact]
    public async Task Attendance_enforces_identity_and_open_record_uniqueness()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        await ApiFactory.Login(client, "EMP001");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/attendance/check-in", new
        {
            employeeId = "EMP002"
        })).StatusCode);
        (await client.PostAsJsonAsync("/api/attendance/check-in", new
        {
            employeeId = "EMP001"
        })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/attendance/check-in", new
        {
            employeeId = "EMP001"
        })).StatusCode);
        (await client.PostAsJsonAsync("/api/attendance/check-out", new
        {
            employeeId = "EMP001"
        })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/attendance/check-out", new
        {
            employeeId = "EMP001"
        })).StatusCode);
    }
    [Fact]
    public async Task Payroll_uses_salary_and_disallows_duplicate_periods_and_get_mutations()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        await ApiFactory.Login(client, "HR001");
        var period = new
        {
            month = 9,
            year = 2026
        };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/payroll/calculate/EMP001", period)).StatusCode);
        (await client.PutAsJsonAsync("/api/payroll/salary/EMP001", new
        {
            basic = 50000,
            allowances = 10000,
            deductions = 5000
        })).EnsureSuccessStatusCode();
        var calculated = await client.PostAsJsonAsync("/api/payroll/calculate/EMP001", period);
        calculated.EnsureSuccessStatusCode();
        JsonElement slip = await calculated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(55000, slip.GetProperty("netPay").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/payroll/calculate/EMP001", period)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/payroll/calculate/EMP001")).StatusCode);
        await ApiFactory.Login(client, "EMP002");
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/payroll/payslips")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/payroll/" + slip.GetProperty("id").GetString())).StatusCode);
    }
    [Fact]
    public async Task Notification_read_receipts_do_not_affect_other_users()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        await ApiFactory.Login(client, "HR001");
        (await client.PostAsJsonAsync("/api/notifications", new
        {
            audience = "All",
            category = "General",
            title = "Notice",
            message = "Hello"
        })).EnsureSuccessStatusCode();
        await ApiFactory.Login(client, "EMP001");
        (await client.PatchAsync("/api/notifications/read-all", null)).EnsureSuccessStatusCode();
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/notifications"))[0].GetProperty("unread").GetBoolean());
        await ApiFactory.Login(client, "EMP002");
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/notifications"))[0].GetProperty("unread").GetBoolean());
    }
}
