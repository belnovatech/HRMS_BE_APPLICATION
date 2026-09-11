using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Api.Controllers;

public sealed record LoginRequest(
    [StringLength(254, MinimumLength = 1)] string Identifier,
    [StringLength(128, MinimumLength = 1)] string Password);

public sealed record OtpRequest([StringLength(254, MinimumLength = 1)] string Identifier);

public sealed record VerifyOtpRequest(
    [StringLength(254, MinimumLength = 1)] string Identifier,
    [RegularExpression("^[0-9]{6}$")] string Otp);

public sealed record ResetPasswordRequest(
    [StringLength(254, MinimumLength = 1)] string Identifier,
    [RegularExpression("^[0-9]{6}$")] string Otp,
    [StringLength(128, MinimumLength = 12)] string NewPassword);

public sealed record OtpData(string Hash, DateTimeOffset ExpiresAt, int Attempts);

public sealed record LoginAttempts(int Count, DateTimeOffset Until);

[EnableRateLimiting("authentication")]
public sealed class AuthController(
    HrmsDbContext database,
    SessionService sessions,
    IEmailSender email,
    TimeProvider clock) : ApiControllerBase
{
    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        UserAccount? user = await FindUser(request.Identifier, cancellationToken);
        string key = SessionService.Digest(user?.Id ?? request.Identifier.Trim().ToLowerInvariant());
        FeatureResource? attempts = await database.FeatureResources.FirstOrDefaultAsync(
            resource => resource.Area == "auth" &&
                        resource.Collection == "attempts" &&
                        resource.ExternalId == key,
            cancellationToken);
        LoginAttempts state = attempts is null
            ? new LoginAttempts(0, clock.GetUtcNow().AddMinutes(15))
            : JsonSerializer.Deserialize<LoginAttempts>(attempts.Payload)!;
        if (state.Until <= clock.GetUtcNow())
        {
            state = new(0, clock.GetUtcNow().AddMinutes(15));
        }

        if (state.Count >= 5)
        {
            return Unauthorized();
        }

        bool active = user is not null && !await IsInactiveEmployee(user.Id, cancellationToken);
        if (!active || user is null || !PasswordSecurity.Verify(user, request.Password))
        {
            string payload = JsonSerializer.Serialize(state with
            {
                Count = state.Count + 1
            });
            if (attempts is null)
            {
                database.Add(new FeatureResource { Area = "auth", Collection = "attempts", ExternalId = key, Payload = payload });
            }
            else
            {
                attempts.Payload = payload;
            }

            await database.SaveChangesAsync(cancellationToken);
            return Unauthorized();
        }
        if (attempts is not null)
        {
            database.Remove(attempts);
        }

        return Ok(new
        {
            token = await sessions.Create(user, cancellationToken),
            expiresIn = 28800,
            user = PublicUser(user)
        });
    }
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        UserAccount? user = await database.Users.FindAsync([userId], cancellationToken);
        return user is null ? Unauthorized() : Ok(PublicUser(user));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        string? digest = User.FindFirstValue("session");
        FeatureResource? row = await database.FeatureResources.FirstOrDefaultAsync(
            resource => resource.Area == "auth" &&
                        resource.Collection == "sessions" &&
                        resource.ExternalId == digest,
            cancellationToken);
        if (row is not null)
        {
            database.Remove(row);
            await database.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    [AllowAnonymous, HttpPost("request-otp")]
    public async Task<IActionResult> RequestOtp(OtpRequest request, CancellationToken cancellationToken)
    {
        if (!email.IsConfigured)
        {
            return StatusCode(503, new
            {
                title = "Email delivery has not been configured."
            });
        }

        UserAccount? user = await FindUser(request.Identifier, cancellationToken);
        if (user is not null)
        {
            string code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            FeatureResource? row = await FindOtp(user.Id, cancellationToken);
            if (row is not null && row.UpdatedAtUtc > clock.GetUtcNow().AddMinutes(-1))
            {
                return Accepted(new
                {
                    message = "If the account exists, a code will be emailed."
                });
            }

            var data = new OtpData(
                PasswordSecurity.Hasher.HashPassword(user, code),
                clock.GetUtcNow().AddMinutes(10),
                0);
            if (row is null)
            {
                row = new FeatureResource
                {
                    Area = "auth",
                    Collection = "otp",
                    ExternalId = user.Id,
                    Payload = JsonSerializer.Serialize(data)
                };
                database.Add(row);
            }
            else
            {
                row.Payload = JsonSerializer.Serialize(data);
                row.UpdatedAtUtc = clock.GetUtcNow();
            }
            await database.SaveChangesAsync(cancellationToken);
            await email.SendOtp(user.Email, code, cancellationToken);
        }
        return Accepted(new
        {
            message = "If the account exists, a code will be emailed."
        });
    }
    [AllowAnonymous, HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp(VerifyOtpRequest request, CancellationToken cancellationToken)
    {
        UserAccount? user = await FindUser(request.Identifier, cancellationToken);
        if (user is null || !await ConsumeOtp(user, request.Otp, cancellationToken))
        {
            return BadRequest(new
            {
                title = "Invalid or expired code."
            });
        }

        return Ok(new
        {
            valid = true,
            token = await sessions.Create(user, cancellationToken),
            user = PublicUser(user)
        });
    }
    [AllowAnonymous, HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!PasswordSecurity.Strong(request.NewPassword))
        {
            return BadRequest(new
            {
                title = "Use 12-128 characters including letters and numbers."
            });
        }

        UserAccount? user = await FindUser(request.Identifier, cancellationToken);
        if (user is null || !await ConsumeOtp(user, request.Otp, cancellationToken))
        {
            return BadRequest(new
            {
                title = "Invalid or expired code."
            });
        }

        database.Entry(user).CurrentValues.SetValues(user with
        {
            Password = PasswordSecurity.Hasher.HashPassword(user, request.NewPassword)
        });
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<bool> ConsumeOtp(
        UserAccount user,
        string code,
        CancellationToken cancellationToken)
    {
        FeatureResource? row = await FindOtp(user.Id, cancellationToken);
        OtpData? data = row is null ? null : JsonSerializer.Deserialize<OtpData>(row.Payload);
        if (row is null || data is null || data.Attempts >= 5 || data.ExpiresAt <= clock.GetUtcNow())
        {
            return false;
        }

        bool valid = PasswordSecurity.Hasher.VerifyHashedPassword(user, data.Hash, code) != PasswordVerificationResult.Failed;
        if (valid)
        {
            database.Remove(row);
        }
        else
        {
            row.Payload = JsonSerializer.Serialize(data with
            {
                Attempts = data.Attempts + 1
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        return valid;
    }

    private Task<FeatureResource?> FindOtp(string userId, CancellationToken cancellationToken)
    {
        return database.FeatureResources.FirstOrDefaultAsync(
            resource => resource.Area == "auth" &&
                        resource.Collection == "otp" &&
                        resource.ExternalId == userId,
            cancellationToken);
    }

    private async Task<UserAccount?> FindUser(
        string identifier,
        CancellationToken cancellationToken)
    {
        string normalized = identifier.Trim().ToLowerInvariant();
        UserAccount? user = await database.Users.FirstOrDefaultAsync(
            account => account.Email.ToLower() == normalized ||
                       account.Username.ToLower() == normalized ||
                       account.Id.ToLower() == normalized,
            cancellationToken);
        if (user is null)
        {
            return null;
        }

        bool isDisabled = await database.FeatureResources.AnyAsync(
            resource => resource.Area == "auth" &&
                        resource.Collection == "disabled" &&
                        resource.ExternalId == user.Id,
            cancellationToken);
        if (isDisabled)
        {
            return null;
        }

        if (await IsInactiveEmployee(user.Id, cancellationToken))
        {
            return null;
        }

        return user;
    }

    private Task<bool> IsInactiveEmployee(string userId, CancellationToken cancellationToken)
    {
        return database.Employees.AnyAsync(
            employee => employee.EmployeeNumber == userId &&
                        (employee.Status == HRMS.Domain.Employees.EmploymentStatus.Inactive ||
                         employee.Status == HRMS.Domain.Employees.EmploymentStatus.Terminated),
            cancellationToken);
    }

    public static object PublicUser(UserAccount user)
    {
        return new
        {
            user.Id,
            user.Email,
            user.Username,
            user.Role,
            user.Name,
            user.Designation,
            user.Department,
            employeeId = user.EmployeeId ?? user.Id,
            user.ReportsTo
        };
    }
}
