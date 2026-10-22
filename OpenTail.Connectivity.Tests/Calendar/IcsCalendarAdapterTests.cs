using System.Net;
using OpenTail.Connectivity.Calendar.Ics;

namespace OpenTail.Connectivity.Tests.Calendar;

public sealed class IcsCalendarAdapterTests
{
    private static readonly DateTimeOffset Jan2026 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string Feed(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static string Event(string uid, string start, string? end = null, string? extra = null) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nSUMMARY:{uid}\r\nDTSTART:{start}\r\n" +
        (end is null ? "" : $"DTEND:{end}\r\n") + (extra ?? "") + "END:VEVENT\r\n";

    private static IcsCalendarAdapter Adapter(string ics) =>
        new("https://example.com/cal.ics", "cal", "acme", new HttpClient(new Fixed(ics)));

    private static async Task<List<CalendarEvent>> Events(IcsCalendarAdapter adapter, CalendarFetchOptions options)
    {
        var list = new List<CalendarEvent>();
        await foreach (var e in adapter.GetEventsAsync(options)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task A_weekly_series_started_years_ago_returns_its_occurrences_in_the_window()
    {
        var ics = Feed(Event("weekly", "20240105T090000Z", "20240105T100000Z", "RRULE:FREQ=WEEKLY\r\n"));

        var events = await Events(Adapter(ics), new CalendarFetchOptions(Start: Jan2026, End: Jan2026.AddDays(14)));

        Assert.Equal([new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 9, 9, 0, 0, TimeSpan.Zero)],
            events.Select(e => e.Start));
        Assert.All(events, e => Assert.Equal(TimeSpan.FromHours(1), e.End - e.Start));
        Assert.All(events, e => Assert.True(e.IsRecurring));
        Assert.Equal("weekly_20260102T090000Z", events[0].EventId);
        Assert.Equal("cal", events[0].CalendarId);
    }

    [Fact]
    public async Task Exdate_and_an_override_are_applied()
    {
        var ics = Feed(
            Event("daily", "20260101T090000Z", "20260101T093000Z", "RRULE:FREQ=DAILY;COUNT=3\r\nEXDATE:20260102T090000Z\r\n"),
            Event("daily", "20260103T150000Z", "20260103T153000Z", "RECURRENCE-ID:20260103T090000Z\r\n"));

        var events = await Events(Adapter(ics), new CalendarFetchOptions(Start: Jan2026, End: Jan2026.AddDays(7)));

        Assert.Equal([new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 3, 15, 0, 0, TimeSpan.Zero)],
            events.Select(e => e.Start));
    }

    [Fact]
    public async Task An_unbounded_rule_over_a_bounded_window_stops_and_honours_max_events()
    {
        var ics = Feed(Event("hourly", "20200101T000000Z", "20200101T001000Z", "RRULE:FREQ=HOURLY\r\n"),
            Event("outside", "20300101T000000Z", "20300101T010000Z"));

        var events = await Events(Adapter(ics), new CalendarFetchOptions(Start: Jan2026, End: Jan2026.AddDays(2), MaxEvents: 5));

        Assert.Equal(5, events.Count);
        Assert.Equal(Jan2026, events[0].Start);
    }

    [Fact]
    public async Task Max_events_applies_after_the_window_and_must_be_positive()
    {
        var ics = Feed(Event("old", "20250101T090000Z", "20250101T100000Z"),
            Event("a", "20260102T090000Z", "20260102T100000Z"), Event("b", "20260103T090000Z", "20260103T100000Z"));
        var adapter = Adapter(ics);

        var one = await Events(adapter, new CalendarFetchOptions(Start: Jan2026, End: Jan2026.AddDays(7), MaxEvents: 1));

        Assert.Equal(["a"], one.Select(e => e.EventId));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Events(adapter, new CalendarFetchOptions(MaxEvents: 0)));
    }

    [Fact]
    public async Task Another_calendar_id_returns_nothing()
    {
        var adapter = Adapter(Feed(Event("a", "20260102T090000Z", "20260102T100000Z")));

        Assert.Null(await adapter.GetEventAsync("other", "a"));
        Assert.Empty(await Events(adapter, new CalendarFetchOptions(CalendarId: "other")));
        Assert.NotNull(await adapter.GetEventAsync("cal", "a"));
    }

    [Fact]
    public async Task An_occurrence_id_can_be_fetched()
    {
        var adapter = Adapter(Feed(Event("weekly", "20240105T090000Z", "20240105T100000Z", "RRULE:FREQ=WEEKLY\r\n")));

        var occurrence = await adapter.GetEventAsync("cal", "weekly_20260102T090000Z");

        Assert.Equal(new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero), occurrence!.Start);
        Assert.Null(await adapter.GetEventAsync("cal", "weekly_20260103T090000Z"));
    }

    [Theory]
    [InlineData("20260102T090000Z", null, 0)]                     // DATE-TIME, no DTEND: ends when it starts
    [InlineData("20260102T090000Z", "DURATION:PT45M\r\n", 45)]    // DURATION
    public void Missing_dtend_follows_rfc_5545(string start, string? extra, int minutes)
    {
        var ics = Feed(Event("e", start, extra: extra));

        var first = IcsSerializer.Deserialize(ics, "acme").Single();
        var again = IcsSerializer.Deserialize(ics, "acme").Single();

        Assert.Equal(TimeSpan.FromMinutes(minutes), first.End - first.Start);
        Assert.Equal(first.End, again.End);
    }

    [Fact]
    public void An_all_day_event_without_dtend_lasts_one_day()
    {
        var ics = Feed("BEGIN:VEVENT\r\nUID:day\r\nDTSTART;VALUE=DATE:20260102\r\nEND:VEVENT\r\n");

        var ev = IcsSerializer.Deserialize(ics, "acme").Single();

        Assert.Equal(TimeSpan.FromDays(1), ev.End - ev.Start);
    }

    [Fact]
    public void An_event_without_dtstart_is_skipped()
    {
        var ics = Feed(Event("ok", "20260102T090000Z"), "BEGIN:VEVENT\r\nUID:nostart\r\nSUMMARY:y\r\nEND:VEVENT\r\n");

        Assert.Equal(["ok"], IcsSerializer.Deserialize(ics, "acme").Select(e => e.EventId));
    }

    private sealed class Fixed(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
