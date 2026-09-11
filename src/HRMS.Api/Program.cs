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
string connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Connection string 'Database' is required.");
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
if (app.Environment.IsDevelopment())
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

public partial class Program;
