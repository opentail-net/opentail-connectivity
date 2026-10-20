namespace OpenTail.Connectivity.Inbox.Graph;

internal static class GraphCalendarMapper
{
    public static CalendarInfo MapCalendar(Calendar c, string tenantContext)
        => new(
            CalendarId: c.Id ?? string.Empty,
            Name: c.Name ?? string.Empty,
            IsDefault: c.IsDefaultCalendar ?? false,
            TenantContext: tenantContext);

    public static CalendarEvent MapEvent(Event e, string calendarId, string tenantContext)
        => new(
            EventId: e.Id ?? string.Empty,
            CalendarId: calendarId,
            Subject: e.Subject ?? string.Empty,
            Start: ParseDtz(e.Start),
            End: ParseDtz(e.End),
            TimeZone: e.Start?.TimeZone ?? "UTC",
            Location: e.Location?.DisplayName,
            Body: e.Body?.Content,
            IsAllDay: e.IsAllDay ?? false,
            IsOnlineMeeting: e.IsOnlineMeeting ?? false,
            OnlineMeetingUrl: e.OnlineMeetingUrl,
            ShowAs: MapShowAs(e.ShowAs),
            Sensitivity: MapSensitivity(e.Sensitivity),
            ResponseStatus: MapResponse(e.ResponseStatus?.Response),
            IsRecurring: e.Recurrence is not null,
            RecurrenceDescription: null,
            Organizer: e.Organizer?.EmailAddress?.Address ?? string.Empty,
            Attendees: (e.Attendees ?? [])
                .Select(a => new EventAttendee(
                    Email: a.EmailAddress?.Address ?? string.Empty,
                    DisplayName: a.EmailAddress?.Name,
                    Type: MapAttendeeType(a.Type),
                    ResponseStatus: MapResponse(a.Status?.Response)))
                .ToArray(),
            Categories: (e.Categories ?? []).ToArray(),
            TenantContext: tenantContext);

    private static DateTimeOffset ParseDtz(DateTimeTimeZone? dtz)
    {
        if (dtz?.DateTime is null) return DateTimeOffset.UtcNow;
        return DateTimeOffset.TryParse(dtz.DateTime, out var dto) ? dto : DateTimeOffset.UtcNow;
    }

    private static string MapShowAs(FreeBusyStatus? status) => status switch
    {
        FreeBusyStatus.Free => "free",
        FreeBusyStatus.Tentative => "tentative",
        FreeBusyStatus.Oof => "outOfOffice",
        FreeBusyStatus.WorkingElsewhere => "workingElsewhere",
        _ => "busy"
    };

    private static string MapSensitivity(Sensitivity? s) => s switch
    {
        Sensitivity.Private => "private",
        Sensitivity.Confidential => "confidential",
        Sensitivity.Personal => "personal",
        _ => "normal"
    };

    private static string MapResponse(ResponseType? r) => r switch
    {
        ResponseType.Accepted => "accepted",
        ResponseType.TentativelyAccepted => "tentative",
        ResponseType.Declined => "declined",
        _ => "notResponded"
    };

    private static string MapAttendeeType(AttendeeType? t) => t switch
    {
        AttendeeType.Optional => "optional",
        AttendeeType.Resource => "resource",
        _ => "required"
    };
}
