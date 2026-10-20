namespace OpenTail.Connectivity.Inbox.Graph;

internal static class GraphCalendarMapper
{
    public static CalendarInfo MapCalendar(Calendar c, string tenantContext)
        => new(
            CalendarId: c.Id ?? string.Empty,
            Name: c.Name ?? string.Empty,
            IsDefault: c.IsDefaultCalendar ?? false,
            TenantContext: tenantContext);

    /// <summary>Null when the event has no readable start or end: a made-up time would look real.</summary>
    public static CalendarEvent? MapEvent(Event e, string calendarId, string tenantContext)
        => CalendarTime.FromZoned(e.Start?.DateTime, e.Start?.TimeZone) is not { } start
           || CalendarTime.FromZoned(e.End?.DateTime, e.End?.TimeZone) is not { } end
            ? null
            : new(
            EventId: e.Id ?? string.Empty,
            CalendarId: calendarId,
            Subject: e.Subject ?? string.Empty,
            Start: start,
            End: end,
            // The adapter asks Graph for UTC times; the event's own zone is kept for display and writes.
            TimeZone: e.OriginalStartTimeZone ?? e.Start?.TimeZone ?? "UTC",
            Location: e.Location?.DisplayName,
            Body: e.Body?.Content,
            IsAllDay: e.IsAllDay ?? false,
            IsOnlineMeeting: e.IsOnlineMeeting ?? false,
            OnlineMeetingUrl: e.OnlineMeetingUrl,
            ShowAs: MapShowAs(e.ShowAs),
            Sensitivity: MapSensitivity(e.Sensitivity),
            ResponseStatus: MapResponse(e.ResponseStatus?.Response),
            IsRecurring: e.Type is EventType.Occurrence or EventType.Exception or EventType.SeriesMaster || e.Recurrence is not null,
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
