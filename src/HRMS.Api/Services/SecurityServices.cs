using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HRMS.Api.Services;

public sealed record SessionData(
    string UserId,
    string PasswordStamp,
    DateTimeOffset ExpiresAt);

public sealed class SessionService(HrmsDbContext database, TimeProvider clock)
{
    public static string Digest(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public async Task<string> Create(UserAccount user, CancellationToken cancellationToken)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new SessionData(
            user.Id,
            Digest(user.Password),
            clock.GetUtcNow().AddHours(8));

        database.FeatureResources.Add(new FeatureResource
        {
            Area = "auth",
            Collection = "sessions",
            ExternalId = Digest(token),
            Payload = JsonSerializer.Serialize(session)
        });

        await database.SaveChangesAsync(cancellationToken);
        return token;
    }
}

public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    HrmsDbContext database,
    TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string token = header[7..].Trim();
        if (token.Length != 64)
        {
            return AuthenticateResult.Fail("Invalid session.");
        }

        string digest = SessionService.Digest(token);
        FeatureResource? row = await database.FeatureResources
            .AsNoTracking()
            .FirstOrDefaultAsync(
                resource => resource.Area == "auth" &&
                            resource.Collection == "sessions" &&
                            resource.ExternalId == digest,
                Context.RequestAborted);
        SessionData? session = row is null ? null : JsonSerializer.Deserialize<SessionData>(row.Payload);
        if (session is null || session.ExpiresAt <= clock.GetUtcNow())
        {
            return AuthenticateResult.Fail("Session expired.");
        }

        UserAccount? user = await database.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(
                account => account.Id == session.UserId,
                Context.RequestAborted);
        if (user is null || SessionService.Digest(user.Password) != session.PasswordStamp)
        {
            return AuthenticateResult.Fail("Session revoked.");
        }

        bool isDisabled = await database.FeatureResources.AnyAsync(
            resource => resource.Area == "auth" &&
                        resource.Collection == "disabled" &&
                        resource.ExternalId == user.Id,
            Context.RequestAborted);
        if (isDisabled)
        {
            return AuthenticateResult.Fail("Account disabled.");
        }

        bool isInactive = await database.Employees.AnyAsync(
            employee => employee.EmployeeNumber == user.Id &&
                        (employee.Status == HRMS.Domain.Employees.EmploymentStatus.Inactive ||
                         employee.Status == HRMS.Domain.Employees.EmploymentStatus.Terminated),
            Context.RequestAborted);
        if (isInactive)
        {
            return AuthenticateResult.Fail("Account inactive.");
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Role, user.Role),
            new("session", digest)
        ];
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

public sealed class AccessScope(IHttpContextAccessor http, HrmsDbContext database)
{
    public string UserId => http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    public bool IsHr => http.HttpContext?.User.IsInRole("hr") == true;

    public bool IsManager => http.HttpContext?.User.IsInRole("manager") == true;

    public string[] VisibleEmployeeIds()
    {
        if (IsHr)
        {
            return database.Users.Select(account => account.Id).ToArray();
        }

        if (!IsManager)
        {
            return [UserId];
        }

        return database.Users
            .Where(account => account.ReportsTo == UserId || account.Id == UserId)
            .Select(account => account.Id)
            .ToArray();
    }

    public bool CanRead(string employeeId)
    {
        return IsHr || VisibleEmployeeIds().Contains(employeeId);
    }

    public bool CanWriteSelf(string employeeId)
    {
        return IsHr || employeeId == UserId;
    }

    public bool CanApprove(string employeeId)
    {
        return employeeId != UserId && (IsHr || (IsManager && CanRead(employeeId)));
    }
}

public static class PasswordSecurity
{
    public static readonly PasswordHasher<UserAccount> Hasher = new();

    public static bool Verify(UserAccount user, string password)
    {
        try
        {
            return Hasher.VerifyHashedPassword(user, user.Password, password) != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool Strong(string value)
    {
        return value.Length is >= 12 and <= 128 &&
               value.Any(char.IsLetter) &&
               value.Any(char.IsDigit);
    }
}
