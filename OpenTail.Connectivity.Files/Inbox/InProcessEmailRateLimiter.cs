namespace OpenTail.Connectivity.Files.Inbox;

/// <summary>Atomically reserves email rate-limit capacity for a send.</summary>
public interface IEmailRateLimiter
{
    /// <summary>Checks the configured limits and reserves one slot, or throws when a limit is reached.</summary>
    EmailRateReservation Reserve();

    /// <summary>Releases a reserved slot when the provider definitely did not accept the email.</summary>
    void Release(EmailRateReservation reservation);

    /// <summary>Checks the configured limits without reserving capacity.</summary>
    void EnsureWithinLimits();
}

/// <summary>A rate-limit slot, tied to the hour and day windows in which it was reserved.</summary>
/// <remarks>A class, not a record: each reservation is its own slot, so two in the same window must not compare equal.</remarks>
public sealed class EmailRateReservation(DateTimeOffset hourWindowStart, DateTimeOffset dayWindowStart)
{
    private int _released;

    public DateTimeOffset HourWindowStart { get; } = hourWindowStart;

    public DateTimeOffset DayWindowStart { get; } = dayWindowStart;

    internal bool TryMarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;
}

/// <summary>In-process email rate limiter. Share a limiter instance to share its counters across senders.</summary>
public sealed class InProcessEmailRateLimiter : IEmailRateLimiter
{
    private readonly EmailSenderPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private int _hourCount;
    private int _dayCount;
    private DateTimeOffset _hourWindowStart;
    private DateTimeOffset _dayWindowStart;

    public InProcessEmailRateLimiter(EmailSenderPolicy policy, TimeProvider? timeProvider = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? TimeProvider.System;
        var now = _timeProvider.GetUtcNow();
        _hourWindowStart = now;
        _dayWindowStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
    }

    public EmailRateReservation Reserve()
    {
        lock (_lock)
        {
            RefreshWindows(_timeProvider.GetUtcNow());
            CheckLimits();
            var reservation = new EmailRateReservation(_hourWindowStart, _dayWindowStart);
            _hourCount++;
            _dayCount++;
            return reservation;
        }
    }

    public void Release(EmailRateReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (!reservation.TryMarkReleased())
            return;
        lock (_lock)
        {
            if (reservation.HourWindowStart == _hourWindowStart && _hourCount > 0)
                _hourCount--;
            if (reservation.DayWindowStart == _dayWindowStart && _dayCount > 0)
                _dayCount--;
        }
    }

    public void EnsureWithinLimits()
    {
        lock (_lock)
        {
            RefreshWindows(_timeProvider.GetUtcNow());
            CheckLimits();
        }
    }

    private void RefreshWindows(DateTimeOffset now)
    {
        if ((now - _hourWindowStart).TotalHours >= 1)
        {
            _hourCount = 0;
            _hourWindowStart = now;
        }

        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        if (today > _dayWindowStart)
        {
            _dayCount = 0;
            _dayWindowStart = today;
        }
    }

    private void CheckLimits()
    {
        if (_policy.MaxEmailsPerHour > 0 && _hourCount >= _policy.MaxEmailsPerHour)
            throw new EmailSendPolicyException($"MaxEmailsPerHour ({_policy.MaxEmailsPerHour}) exceeded");

        if (_policy.MaxEmailsPerDay > 0 && _dayCount >= _policy.MaxEmailsPerDay)
            throw new EmailSendPolicyException($"MaxEmailsPerDay ({_policy.MaxEmailsPerDay}) exceeded");
    }
}
