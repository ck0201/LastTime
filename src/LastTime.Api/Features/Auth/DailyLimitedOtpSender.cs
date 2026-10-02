using LastTime.Api.Domain;

namespace LastTime.Api.Features.Auth;

/// <summary>
/// Stops accidental over-sending: after <c>dailyLimit</c> emails in one UTC day it hands nothing more to the provider.
/// Provider-neutral, in memory and per process, which is right for a single-instance MVP (a restart or a second
/// instance has its own count). Every attempt counts, including ones the provider later rejects.
/// </summary>
public sealed class DailyLimitedOtpSender(
    IOtpSender inner, int dailyLimit, TimeProvider time, ILogger<DailyLimitedOtpSender> logger) : IOtpSender
{
    private readonly object _gate = new();
    private DateOnly _day;
    private int _sentToday;

    public Task SendAsync(User user, string purpose, string code, CancellationToken ct)
    {
        if (!TryReserve())
        {
            logger.LogWarning(
                "Daily email limit ({Limit}) reached: the {Purpose} email for user {UserId} was not sent.",
                dailyLimit, purpose, user.Id);
            return Task.CompletedTask;
        }

        return inner.SendAsync(user, purpose, code, ct);
    }

    private bool TryReserve()
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        lock (_gate)
        {
            if (today != _day)
            {
                _day = today;
                _sentToday = 0;
            }
            if (_sentToday >= dailyLimit) return false;
            _sentToday++;
            return true;
        }
    }
}
