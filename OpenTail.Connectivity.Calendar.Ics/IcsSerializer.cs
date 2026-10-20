using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using ICalCalendar = Ical.Net.Calendar;
using ICalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using OurCalendarEvent = OpenTail.Connectivity.Files.Models.CalendarEvent;

namespace OpenTail.Connectivity.Calendar.Ics;

public static class IcsSerializer
{
    public static string Serialize(OurCalendarEvent ev)
    {
        var cal = new ICalCalendar();
        var icalEv = new ICalEvent
        {
            Summary = ev.Subject,
            Description = ev.Body,
            Location = ev.Location,
            DtStart = new CalDateTime(ev.Start.UtcDateTime),
            DtEnd = new CalDateTime(ev.End.UtcDateTime),
            IsAllDay = ev.IsAllDay,
            Uid = ev.EventId
        };

        foreach (var attendee in ev.Attendees)
        {
            icalEv.Attendees.Add(new Attendee
            {
                Value = new Uri($"mailto:{attendee.Email}"),
                CommonName = attendee.DisplayName,
                Role = attendee.Type == "optional" ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT"
            });
        }

        if (!string.IsNullOrWhiteSpace(ev.Organizer))
        {
            icalEv.Organizer = new Organizer
            {
                Value = new Uri($"mailto:{ev.Organizer}")
            };
        }

        cal.Events.Add(icalEv);
        return new CalendarSerializer().SerializeToString(cal);
    }

    public static IReadOnlyList<OurCalendarEvent> Deserialize(string ics, string tenantContext)
    {
        var calendar = ICalCalendar.Load(ics);
        var results = new List<OurCalendarEvent>(calendar.Events.Count);
        foreach (var e in calendar.Events)
        {
            try
            {
                results.Add(MapEvent(e, tenantContext));
            }
            catch
            {
                // Skip malformed events rather than losing the entire feed.
            }
        }
        return results;
    }

    private static OurCalendarEvent MapEvent(ICalEvent e, string tenantContext)
        => new(
            EventId: e.Uid ?? Guid.NewGuid().ToString(),
            CalendarId: string.Empty,
            Subject: e.Summary ?? string.Empty,
            Start: e.DtStart is not null
                ? new DateTimeOffset(e.DtStart.AsUtc) : DateTimeOffset.UtcNow,
            End: e.DtEnd is not null
                ? new DateTimeOffset(e.DtEnd.AsUtc) : DateTimeOffset.UtcNow,
            TimeZone: e.DtStart?.TzId ?? "UTC",
            Location: e.Location,
            Body: e.Description,
            IsAllDay: e.IsAllDay,
            IsOnlineMeeting: false,
            OnlineMeetingUrl: null,
            ShowAs: "busy",
            Sensitivity: "normal",
            ResponseStatus: "notResponded",
            IsRecurring: e.RecurrenceRules is { Count: > 0 },
            RecurrenceDescription: e.RecurrenceRules?.FirstOrDefault()?.ToString(),
            Organizer: e.Organizer?.Value?.UserInfo ?? string.Empty,
            Attendees: (e.Attendees ?? [])
                .Select(a => new EventAttendee(
                    Email: a.Value?.UserInfo ?? string.Empty,
                    DisplayName: a.CommonName,
                    Type: "OPT-PARTICIPANT".Equals(a.Role, StringComparison.OrdinalIgnoreCase)
                        ? "optional" : "required",
                    ResponseStatus: MapPartStat(a.ParticipationStatus)))
                .ToArray(),
            Categories: [],
            TenantContext: tenantContext);

    private static string MapPartStat(string? s) => s?.ToUpperInvariant() switch
    {
        "ACCEPTED" => "accepted",
        "TENTATIVE" => "tentative",
        "DECLINED" => "declined",
        _ => "notResponded"
    };
}
