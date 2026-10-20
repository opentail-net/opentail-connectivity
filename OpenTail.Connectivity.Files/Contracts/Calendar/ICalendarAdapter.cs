namespace OpenTail.Connectivity.Files.Contracts;

public interface ICalendarAdapter
{
    IAsyncEnumerable<CalendarInfo> ListCalendarsAsync(CancellationToken ct = default);

    IAsyncEnumerable<CalendarEvent> GetEventsAsync(
        CalendarFetchOptions options, CancellationToken ct = default);

    Task<CalendarEvent?> GetEventAsync(
        string calendarId, string eventId, CancellationToken ct = default);

    Task<string> CreateEventAsync(CalendarEvent draft, CancellationToken ct = default);

    Task UpdateEventAsync(CalendarEvent updated, CancellationToken ct = default);

    Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default);

    Task RespondToEventAsync(
        string calendarId, string eventId,
        string response, string? comment = null,
        CancellationToken ct = default);
}
