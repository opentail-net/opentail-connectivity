namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>Reading a calendar. Hand this, not <see cref="ICalendarAdapter"/>, to code that should only read.</summary>
public interface ICalendarReader
{
    IAsyncEnumerable<CalendarInfo> ListCalendarsAsync(CancellationToken ct = default);

    IAsyncEnumerable<CalendarEvent> GetEventsAsync(
        CalendarFetchOptions options, CancellationToken ct = default);

    Task<CalendarEvent?> GetEventAsync(
        string calendarId, string eventId, CancellationToken ct = default);
}

/// <summary>
/// Reading and changing a calendar. The Graph and Gmail adapters check every change against their
/// <see cref="CalendarWritePolicy"/> (default: none allowed) before calling the provider.
/// </summary>
public interface ICalendarAdapter : ICalendarReader
{

    Task<string> CreateEventAsync(CalendarEvent draft, CancellationToken ct = default);

    Task UpdateEventAsync(CalendarEvent updated, CancellationToken ct = default);

    Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default);

    Task RespondToEventAsync(
        string calendarId, string eventId,
        string response, string? comment = null,
        CancellationToken ct = default);
}
