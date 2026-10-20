namespace OpenTail.Connectivity.Files.Inbox;

/// <summary>A wrapper for handing a calendar to automation that must only read.</summary>
public sealed class ReadOnlyCalendarAdapter : ICalendarAdapter
{
    private readonly ICalendarAdapter _inner;

    public ReadOnlyCalendarAdapter(ICalendarAdapter inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public IAsyncEnumerable<CalendarInfo> ListCalendarsAsync(CancellationToken ct = default)
        => _inner.ListCalendarsAsync(ct);

    public IAsyncEnumerable<CalendarEvent> GetEventsAsync(CalendarFetchOptions options, CancellationToken ct = default)
        => _inner.GetEventsAsync(options, ct);

    public Task<CalendarEvent?> GetEventAsync(string calendarId, string eventId, CancellationToken ct = default)
        => _inner.GetEventAsync(calendarId, eventId, ct);

    public Task<string> CreateEventAsync(CalendarEvent draft, CancellationToken ct = default)
        => throw new NotSupportedException("This calendar is read-only.");

    public Task UpdateEventAsync(CalendarEvent updated, CancellationToken ct = default)
        => throw new NotSupportedException("This calendar is read-only.");

    public Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default)
        => throw new NotSupportedException("This calendar is read-only.");

    public Task RespondToEventAsync(string calendarId, string eventId, string response, string? comment = null, CancellationToken ct = default)
        => throw new NotSupportedException("This calendar is read-only.");
}
