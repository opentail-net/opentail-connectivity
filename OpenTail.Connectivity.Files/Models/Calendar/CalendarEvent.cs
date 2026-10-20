namespace OpenTail.Connectivity.Files.Models;

public sealed record CalendarEvent(
    string EventId,
    string CalendarId,
    string Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    string TimeZone,
    string? Location,
    string? Body,
    bool IsAllDay,
    bool IsOnlineMeeting,
    string? OnlineMeetingUrl,
    string ShowAs,              // free | tentative | busy | outOfOffice
    string Sensitivity,         // normal | private | confidential
    string ResponseStatus,      // accepted | tentative | declined | notResponded
    bool IsRecurring,
    string? RecurrenceDescription,
    string Organizer,
    IReadOnlyList<EventAttendee> Attendees,
    IReadOnlyList<string> Categories,
    string TenantContext);
