using Google.Apis.Calendar.v3.Data;
using OpenTail.Connectivity.Inbox.Gmail;
using GEventAttendee = Google.Apis.Calendar.v3.Data.EventAttendee;

namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class GmailCalendarMapperTests
{
    [Fact]
    public void Response_status_uses_the_self_attendee_instead_of_the_creator()
    {
        var ev = new Event
        {
            Creator = new Event.CreatorData { Email = "boss@example.com" },
            Attendees =
            [
                new GEventAttendee { Email = "boss@example.com", ResponseStatus = "accepted" },
                new GEventAttendee { Email = "me@example.com", Self = true, ResponseStatus = "declined" }
            ]
        };

        var mapped = GmailCalendarMapper.MapEvent(Timed(ev), "primary", "tenant")!;

        Assert.Equal("declined", mapped.ResponseStatus);
    }

    [Fact]
    public void Response_status_falls_back_to_the_account_email_when_no_attendee_is_marked_self()
    {
        var ev = new Event
        {
            Attendees =
            [
                new GEventAttendee { Email = "someone@example.com", ResponseStatus = "accepted" },
                new GEventAttendee { Email = "me@example.com", ResponseStatus = "tentative" }
            ]
        };

        var mapped = GmailCalendarMapper.MapEvent(Timed(ev), "primary", "tenant", "ME@example.com")!;

        Assert.Equal("tentative", mapped.ResponseStatus);
    }

    [Fact]
    public void Response_status_is_not_responded_when_the_account_does_not_match()
    {
        var ev = new Event
        {
            Attendees = [new GEventAttendee { Email = "someone@example.com", ResponseStatus = "accepted" }]
        };

        var mapped = GmailCalendarMapper.MapEvent(Timed(ev), "primary", "tenant", "me@example.com")!;

        Assert.Equal("notResponded", mapped.ResponseStatus);
    }

    [Fact]
    public void Recurring_masters_occurrences_and_single_events_are_classified()
    {
        var master = new Event { Recurrence = ["RRULE:FREQ=WEEKLY"] };
        var occurrence = new Event { RecurringEventId = "master" };
        var single = new Event();

        Assert.True(GmailCalendarMapper.MapEvent(Timed(master), "primary", "tenant")!.IsRecurring);
        Assert.True(GmailCalendarMapper.MapEvent(Timed(occurrence), "primary", "tenant")!.IsRecurring);
        Assert.False(GmailCalendarMapper.MapEvent(Timed(single), "primary", "tenant")!.IsRecurring);
    }

    private static Event Timed(Event e)
    {
        e.Start = new EventDateTime { DateTimeRaw = "2026-10-20T09:00:00Z" };
        e.End = new EventDateTime { DateTimeRaw = "2026-10-20T10:00:00Z" };
        return e;
    }
}
