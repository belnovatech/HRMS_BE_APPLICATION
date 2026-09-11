using System.Net;
using System.Net.Mail;

namespace HRMS.Api.Services;

public interface IEmailSender
{
    bool IsConfigured => true;

    Task SendOtp(string address, string code, CancellationToken cancellationToken);
}

public sealed class SmtpEmailSender(IConfiguration configuration) : IEmailSender
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(configuration["Email:Host"]) &&
        !string.IsNullOrWhiteSpace(configuration["Email:From"]);

    public async Task SendOtp(
        string address,
        string code,
        CancellationToken cancellationToken)
    {
        string host = configuration["Email:Host"]
            ?? throw new InvalidOperationException("Email delivery is not configured.");
        string from = configuration["Email:From"]
            ?? throw new InvalidOperationException("Email sender is not configured.");
        using var client = new SmtpClient(host, configuration.GetValue("Email:Port", 587))
        {
            EnableSsl = true
        };

        if (configuration["Email:Username"] is { Length: > 0 } username)
        {
            client.Credentials = new NetworkCredential(username, configuration["Email:Password"]);
        }

        using var message = new MailMessage(
            from,
            address,
            "HRMS verification code",
            $"Your verification code is {code}. It expires in 10 minutes. If you did not request it, ignore this email.");
        await client.SendMailAsync(message, cancellationToken);
    }
}
