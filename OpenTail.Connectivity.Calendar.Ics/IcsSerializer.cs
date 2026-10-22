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

    /// <summary>
    /// Events as stored in the feed: a recurring event appears once, as its series (with <c>IsRecurring</c>), at its first
    /// occurrence. Use <see cref="Occurrences"/> for the occurrences in a time window.
    /// </summary>
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

    /// <summary>Upper bound on occurrences expanded per call, so a dense rule over a long window cannot run away.</summary>
    public const int MaxOccurrences = 10_000;

    /// <summary>
    /// Every event and recurrence occurrence overlapping [<paramref name="from"/>, <paramref name="to"/>], in start order, with
    /// EXDATE and RECURRENCE-ID overrides applied. An occurrence of a recurring event has the id <c>uid_yyyyMMddTHHmmssZ</c>
    /// (its UTC start); a single event keeps its UID.
    /// </summary>
    public static IReadOnlyList<OurCalendarEvent> Occurrences(string ics, string tenantContext, DateTimeOffset from, DateTimeOffset to)
    {
        var calendar = ICalCalendar.Load(ics);
        // An occurrence that starts before the window can still overlap it: look back by the longest event (capped).
        var lookBack = TimeSpan.FromDays(1);
        foreach (var e in calendar.Events)
        {
            if (e.DtStart is null) continue;
            var length = End(e) - e.DtStart.AsUtc;
            if (length > lookBack) lookBack = length < TimeSpan.FromDays(31) ? length : TimeSpan.FromDays(31);
        }

        var results = new List<OurCalendarEvent>();
        var occurrences = calendar.GetOccurrences<ICalEvent>(new CalDateTime(from.UtcDateTime.Add(-lookBack), "UTC"), new CalDateTime(to.UtcDateTime.AddSeconds(1), "UTC")); // the end is exclusive
        foreach (var occurrence in occurrences.OrderBy(o => o.Period.StartTime.AsUtc))
        {
            if (occurrence.Source is not ICalEvent source || source.DtStart is null) continue;
            var start = new DateTimeOffset(DateTime.SpecifyKind(occurrence.Period.StartTime.AsUtc, DateTimeKind.Utc));
            var end = occurrence.Period.EndTime is { } endTime
                ? new DateTimeOffset(DateTime.SpecifyKind(endTime.AsUtc, DateTimeKind.Utc))
                : start + (End(source) - source.DtStart.AsUtc);
            if (end < from || start > to) continue;
            OurCalendarEvent mapped;
            try
            {
                mapped = MapEvent(source, tenantContext);
            }
            catch
            {
                continue; // malformed: skipped, as in Deserialize
            }
            var series = source.RecurrenceRules is { Count: > 0 } || source.RecurrenceDates is { Count: > 0 } || source.RecurrenceId is not null;
            results.Add(mapped with
            {
                EventId = series ? $"{mapped.EventId}_{start.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}" : mapped.EventId,
                Start = start,
                End = end,
                IsRecurring = series,
            });
            if (results.Count >= MaxOccurrences) break;
        }
        return results;
    }

    // RFC 5545 3.6.1: DTEND, else DTSTART + DURATION, else one day for a DATE start, else the start itself.
    private static DateTime End(ICalEvent e)
    {
        if (e.DtEnd is not null) return e.DtEnd.AsUtc;
        if (e.Properties.ContainsKey("DURATION")) return e.DtStart.AsUtc + e.Duration;
        return e.DtStart.HasTime ? e.DtStart.AsUtc : e.DtStart.AsUtc.AddDays(1);
    }

    private static OurCalendarEvent MapEvent(ICalEvent e, string tenantContext)
        => new(
            // Ical.Net gives a VEVENT without a UID (invalid) a random one, so such an event's id changes on every read.
            EventId: e.Uid,
            CalendarId: string.Empty,
            Subject: e.Summary ?? string.Empty,
            // A VEVENT without DTSTART is malformed: it is skipped rather than given a made-up time.
            Start: new DateTimeOffset(DateTime.SpecifyKind((e.DtStart ?? throw new InvalidDataException("A VEVENT has no DTSTART.")).AsUtc, DateTimeKind.Utc)),
            End: new DateTimeOffset(DateTime.SpecifyKind(End(e), DateTimeKind.Utc)),
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
