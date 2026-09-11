using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HRMS.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
namespace HRMS.Api.IntegrationTests;
public partial class ApiTests
{
    [Fact]
    public async Task Production_demo_accounts_cannot_use_password_or_otp_login()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();
            var account = db.Users.Find("HR001")!;
            db.Entry(account).CurrentValues.SetValues(account with
            {
                Password = "password123"
            });
            db.SaveChanges();
            var environment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            environment.EnvironmentName = "Production";
            await DatabaseInitialization.SecureAccounts(db, environment, new ConfigurationBuilder().Build());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new
        {
            identifier = "HR001",
            password = "password123"
        })).StatusCode);
        (await client.PostAsJsonAsync("/api/auth/request-otp", new
        {
            identifier = "HR001"
        })).EnsureSuccessStatusCode();
        Assert.Null(factory.Email.Code);
    }
    [Fact]
    public async Task Files_are_private_and_reject_executables()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        await ApiFactory.Login(client, "EMP001");
        using var invalid = new MultipartFormDataContent();
        invalid.Add(new StringContent("data"), "file", "bad.exe");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/files", invalid)).StatusCode);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("private document"), "file", "sample.txt");
        var uploaded = await client.PostAsync("/api/files", content);
        uploaded.EnsureSuccessStatusCode();
        string id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await ApiFactory.Login(client, "EMP002");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/files/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/files/{id}")).StatusCode);
        await ApiFactory.Login(client, "EMP001");
        Assert.Equal("private document", await client.GetStringAsync($"/api/files/{id}"));
        (await client.DeleteAsync($"/api/files/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/files/{id}")).StatusCode);
    }
    [Fact]
    public async Task Unconfigured_biometric_sync_does_not_claim_success()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        await ApiFactory.Login(client, "HR001");
        var created = await client.PostAsJsonAsync("/api/biometric/devices", new
        {
            id = "device1",
            name = "Office",
            status = "Connected"
        });
        created.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/biometric/devices/device1/sync", null)).StatusCode);
        JsonElement device = await client.GetFromJsonAsync<JsonElement>("/api/biometric/devices/device1");
        Assert.Equal("Not synced", device.GetProperty("status").GetString());
        Assert.False(device.TryGetProperty("lastSync", out _));
    }
    [Fact]
    public async Task Correction_approval_changes_attendance_and_rejects_replay()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        await ApiFactory.Login(client, "EMP001");
        var created = await client.PostAsJsonAsync("/api/attendance/corrections", new
        {
            date = "2026-09-01",
            checkIn = "09:00:00",
            checkOut = "17:30:00",
            reason = "Missed punch"
        });
        created.EnsureSuccessStatusCode();
        string id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await ApiFactory.Login(client, "HR001");
        (await client.PostAsJsonAsync($"/api/attendance/corrections/{id}/decision", new
        {
            status = "Approved"
        })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/attendance/corrections/{id}/decision", new
        {
            status = "Approved"
        })).StatusCode);
        JsonElement rows = await client.GetFromJsonAsync<JsonElement>("/api/attendance?employeeId=EMP001&date=2026-09-01");
        Assert.Equal("8h 30m", rows[0].GetProperty("workingHours").GetString());
    }
    [Fact]
    public async Task Account_lockout_expires_and_cannot_be_bypassed_with_an_alias()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        for (int i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new
            {
                identifier = "EMP001",
                password = "incorrect"
            })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new
        {
            identifier = "arjun@belnova.com",
            password = "SecureTest123!"
        })).StatusCode);
        factory.Clock.Now = factory.Clock.Now.AddMinutes(16);
        await ApiFactory.Login(client, "EMP001");
    }
    [Fact]
    public async Task Leave_approval_requires_sufficient_entitlement()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        await ApiFactory.Login(client, "EMP001");
        var created = await client.PostAsJsonAsync("/api/leave", new
        {
            employeeId = "EMP001",
            leaveType = "Casual",
            startDate = "2026-11-01",
            endDate = "2026-11-20",
            reason = "Personal"
        });
        created.EnsureSuccessStatusCode();
        string id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await ApiFactory.Login(client, "HR001");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"/api/leave/{id}/decision", new
        {
            status = "Approved"
        })).StatusCode);
    }
    [Fact]
    public async Task Resource_validation_and_auditing_are_enforced()
    {
        using var factory = new ApiFactory();
        using var client = factory.Start();
        await ApiFactory.Login(client, "HR001");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/organization/departments", new
        {
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/settings/not-a-collection")).StatusCode);
        (await client.PostAsJsonAsync("/api/organization/departments", new
        {
            name = "Engineering"
        })).EnsureSuccessStatusCode();
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/organization/departments")).EnumerateArray());
        Assert.NotEmpty((await client.GetFromJsonAsync<JsonElement>("/api/audit")).EnumerateArray());
        await ApiFactory.Login(client, "EMP001");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/audit")).StatusCode);
    }
}
