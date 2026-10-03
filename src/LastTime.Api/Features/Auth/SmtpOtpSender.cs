using LastTime.Api.Domain;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Options;

namespace LastTime.Api.Features.Auth;

/// <summary>
/// How a finished message leaves the process. In production this is <see cref="MailKitSmtpTransport.SendAsync"/>;
/// tests substitute a recording one, so no test ever opens a real SMTP connection.
/// </summary>
public delegate Task SmtpTransport(SmtpOptions options, MimeMessage message, CancellationToken ct);

/// <summary>The real transport: MailKit over STARTTLS (the connection must be upgraded before credentials are sent).</summary>
public static class MailKitSmtpTransport
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        await client.ConnectAsync(options.Host, options.Port, SecureSocketOptions.StartTls, ct);
        await client.AuthenticateAsync(options.Username, options.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}

/// <summary>
/// Emails one-time codes over SMTP. Never throws for delivery problems and never logs the code, the password or the
/// recipient: a failure is logged (by type and SMTP status only) and the caller carries on, so API responses do not change.
/// </summary>
public sealed class SmtpOtpSender(SmtpTransport transport, IOptions<SmtpOptions> options, ILogger<SmtpOtpSender> logger) : IOtpSender
{
    public async Task SendAsync(User user, string purpose, string code, CancellationToken ct)
    {
        OtpEmail.Message content;
        try
        {
            content = OtpEmail.For(purpose, code);
        }
        catch (ArgumentException)
        {
            logger.LogError("No email is defined for OTP purpose {Purpose}; nothing was sent.", purpose);
            return;
        }

        var smtp = options.Value;
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(smtp.SenderName ?? "", smtp.SenderEmail));
            message.To.Add(MailboxAddress.Parse(user.Email));
            message.Subject = content.Subject;
            message.Body = new BodyBuilder { TextBody = content.Text, HtmlBody = content.Html }.ToMessageBody();

            await transport(smtp, message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller gave up (for example the request was aborted); that is not a delivery failure
        }
        catch (Exception ex)
        {
            // Only the kind of failure is logged. The exception text can echo the recipient or server replies, so it is not.
            logger.LogError(
                "Sending the {Purpose} email to user {UserId} over SMTP failed ({ExceptionType}, SMTP status {SmtpStatus}).",
                purpose, user.Id, ex.GetType().Name, (ex as SmtpCommandException)?.StatusCode.ToString() ?? "none");
        }
    }
}
