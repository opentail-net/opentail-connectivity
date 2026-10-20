using GCalAttendee = Google.Apis.Calendar.v3.Data.EventAttendee;
using Google.Apis.Calendar.v3.Data;
using OurEventAttendee = OpenTail.Connectivity.Files.Models.EventAttendee;

namespace OpenTail.Connectivity.Inbox.Gmail;

internal static class GmailCalendarMapper
{
    public static CalendarInfo MapCalendar(CalendarListEntry c, string tenantContext)
        => new(
            CalendarId: c.Id ?? string.Empty,
            Name: c.Summary ?? string.Empty,
            IsDefault: c.Primary ?? false,
            TenantContext: tenantContext);

    /// <param name="calendarTimeZone">The calendar's zone (from the events list), for all-day dates and times without an offset.</param>
    /// <returns>Null when the event has no readable start or end: a made-up time would look real.</returns>
    public static CalendarEvent? MapEvent(Event e, string calendarId, string tenantContext, string? accountEmail = null, string? calendarTimeZone = null)
        => ParseEventDateTime(e.Start, calendarTimeZone) is not { } start || ParseEventDateTime(e.End, calendarTimeZone) is not { } end
            ? null
            : new(
            EventId: e.Id ?? string.Empty,
            CalendarId: calendarId,
            Subject: e.Summary ?? string.Empty,
            Start: start,
            End: end, // all-day: exclusive, midnight starting the day after
            TimeZone: e.Start?.TimeZone ?? calendarTimeZone ?? "UTC",
            Location: e.Location,
            Body: e.Description,
            IsAllDay: e.Start?.Date is not null,
            IsOnlineMeeting: e.ConferenceData is not null,
            OnlineMeetingUrl: e.HangoutLink,
            ShowAs: MapTransparency(e.Transparency),
            Sensitivity: MapVisibility(e.Visibility),
            ResponseStatus: MapSelfStatus(e.Attendees, accountEmail),
            IsRecurring: !string.IsNullOrWhiteSpace(e.RecurringEventId) || e.Recurrence is { Count: > 0 },
            RecurrenceDescription: e.Recurrence is { Count: > 0 } ? e.Recurrence[0] : null,
            Organizer: e.Organizer?.Email ?? string.Empty,
            Attendees: (e.Attendees ?? [])
                .Select(a => new OurEventAttendee(
                    Email: a.Email ?? string.Empty,
                    DisplayName: a.DisplayName,
                    Type: a.Optional == true ? "optional" : "required",
                    ResponseStatus: MapAttendeeStatus(a.ResponseStatus)))
                .ToArray(),
            Categories: [],
            TenantContext: tenantContext);

    // A timed value normally carries its offset; one without uses the event's or the calendar's zone. An all-day value is a date,
    // read as midnight in that zone, never in the machine's.
    private static DateTimeOffset? ParseEventDateTime(EventDateTime? edt, string? calendarTimeZone)
    {
        if (edt is null)
            return null;
        var zone = edt.TimeZone ?? calendarTimeZone;
        return !string.IsNullOrWhiteSpace(edt.DateTimeRaw)
            ? CalendarTime.FromZoned(edt.DateTimeRaw, zone)
            : CalendarTime.FromDate(edt.Date, zone);
    }

    private static string MapTransparency(string? t) => t switch
    {
        "transparent" => "free",
        _ => "busy"
    };

    private static string MapVisibility(string? v) => v switch
    {
        "private" => "private",
        "confidential" => "confidential",
        _ => "normal"
    };

    private static string MapSelfStatus(
        IList<GCalAttendee>? attendees, string? accountEmail)
    {
        if (attendees is null) return "notResponded";
        var self = attendees.FirstOrDefault(a => a.Self == true);
        if (self is null && accountEmail is not null)
            self = attendees.FirstOrDefault(a => string.Equals(a.Email, accountEmail, StringComparison.OrdinalIgnoreCase));
        return MapAttendeeStatus(self?.ResponseStatus);
    }

    private static string MapAttendeeStatus(string? s) => s switch
    {
        "accepted" => "accepted",
        "tentative" => "tentative",
        "declined" => "declined",
        _ => "notResponded"
    };
}
