using System.Threading.RateLimiting;
using HRMS.Api.Middleware;
using HRMS.Api.Services;
using HRMS.Application.Abstractions;
using HRMS.Application.Abstractions.Persistence;
using HRMS.Application.Features.Employees.CreateEmployee;
using HRMS.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.SwaggerDoc("v1", new()
    {
        Title = "HRMS API",
        Version = "v1"
    }));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AccessScope>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddHttpClient("biometric", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services
    .AddAuthentication("Bearer")
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>("Bearer", _ => { });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("authentication", context =>
    {
        string clientAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            clientAddress,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 15,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

string[] origins = builder.Configuration
    .GetSection("Cors:Origins")
    .Get<string[]>() ?? ["http://localhost:3000"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(origins)
    .AllowAnyHeader()
    .AllowAnyMethod()));
builder.Services.AddInfrastructure();
// ✅ Connection string fix with nullability
string? connectionString = builder.Configuration.GetConnectionString("Database");

// If Render injects DATABASE_URL in URI format, convert it
string? databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrWhiteSpace(databaseUrl))
{
    connectionString = databaseUrl;
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'Database' is required.");
}

connectionString = NormalizePostgresConnectionString(connectionString);
builder.Services.AddDbContext<HrmsDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<HrmsStore>();
builder.Services.AddScoped<EfEmployeeRepository>();
builder.Services.AddScoped<IEmployeeRepository>(provider => provider.GetRequiredService<EfEmployeeRepository>());
builder.Services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<EfEmployeeRepository>());
builder.Services.AddScoped<CreateEmployeeHandler>();

WebApplication app = builder.Build();

using (IServiceScope scope = app.Services.CreateScope())
{
    HrmsDbContext database = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();
    if (!app.Environment.IsEnvironment("Testing"))
    {
        await database.Database.MigrateAsync();
        await DatabaseInitialization.SecureAccounts(database, app.Environment, app.Configuration);
    }
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new()
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();
app.MapControllers();

app.Run();

static string NormalizePostgresConnectionString(string connectionString)
{
    if (!connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return connectionString;
    }

    var uri = new Uri(connectionString);
    string[] credentials = uri.UserInfo.Split(':', 2);
    if (credentials.Length != 2 || string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
    {
        throw new InvalidOperationException("The PostgreSQL connection URL is invalid.");
    }

    return new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
        Username = Uri.UnescapeDataString(credentials[0]),
        Password = Uri.UnescapeDataString(credentials[1])
    }.ConnectionString;
}

public partial class Program;
