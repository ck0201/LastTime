using System.Collections.Concurrent;
using LastTime.Api.Domain;
using LastTime.Api.Features.Auth;

namespace LastTime.Api.Tests;

/// <summary>A clock the tests move by hand, so OTP expiry and cooldowns never depend on real waiting.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset now) => _now = now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Stands in for email delivery: remembers every code the API hands to the sender, so tests can use it.</summary>
public sealed class CapturingOtpSender : IOtpSender
{
    public sealed record Sent(Guid UserId, string Email, string Purpose, string Code);

    private readonly ConcurrentQueue<Sent> _sent = new();

    public IReadOnlyList<Sent> All => _sent.ToArray();
    public void Clear() => _sent.Clear();

    public Task SendAsync(User user, string purpose, string code, CancellationToken ct)
    {
        _sent.Enqueue(new Sent(user.Id, user.Email, purpose, code));
        return Task.CompletedTask;
    }
}
