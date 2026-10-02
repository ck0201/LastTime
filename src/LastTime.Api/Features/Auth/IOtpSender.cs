using LastTime.Api.Domain;

namespace LastTime.Api.Features.Auth;

/// <summary>
/// Where a freshly issued one-time code goes. The endpoints hand every plaintext code to this and never put it in an
/// HTTP response or a log. Real email delivery is a later step and will replace <see cref="NotConfiguredOtpSender"/>.
/// </summary>
public interface IOtpSender
{
    Task SendAsync(User user, string purpose, string code, CancellationToken ct);
}

/// <summary>
/// Placeholder until email delivery exists: it deliberately does nothing, so no code is sent, stored or logged anywhere.
/// </summary>
public sealed class NotConfiguredOtpSender : IOtpSender
{
    public Task SendAsync(User user, string purpose, string code, CancellationToken ct) => Task.CompletedTask;
}
