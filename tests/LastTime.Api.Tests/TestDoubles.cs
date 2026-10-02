using System.Collections.Concurrent;
using LastTime.Api.Domain;
using LastTime.Api.Features.Auth;

namespace LastTime.Api.Tests;

/// <summary>
/// A clock the tests move by hand, so OTP expiry, cooldowns and response-time padding never depend on real waiting.
/// Time is virtual: work takes no time, and a delay (Task.Delay with this provider) completes at once while moving the
/// clock forward by exactly the delay. So "how long did that take" is an exact number, not a wall-clock measurement.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private long _ticks; // monotonic, like Stopwatch timestamps; only ever moves forward

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }

    /// <summary>Jumps the calendar clock (the monotonic timestamp is unaffected).</summary>
    public void Set(DateTimeOffset now) { lock (_gate) _now = now; }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
            _ticks += by.Ticks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new VirtualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class VirtualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan) return true; // switched off
            clock.Advance(dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state)); // never inside the caller, as a real timer
            return true;
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Stands in for email delivery: remembers every code the API hands to the sender, so tests can use it.</summary>
public sealed class CapturingOtpSender : IOtpSender
{
    public sealed record Sent(Guid UserId, string Email, string Purpose, string Code);

    private readonly ConcurrentQueue<Sent> _sent = new();

    public IReadOnlyList<Sent> All => _sent.ToArray();

    /// <summary>Runs inside every send, so a test can make "sending" slow (move the clock) or cancel the request.</summary>
    public Action? OnSend { get; set; }

    public void Clear()
    {
        _sent.Clear();
        OnSend = null;
    }

    public Task SendAsync(User user, string purpose, string code, CancellationToken ct)
    {
        _sent.Enqueue(new Sent(user.Id, user.Email, purpose, code));
        OnSend?.Invoke();
        return Task.CompletedTask;
    }
}
