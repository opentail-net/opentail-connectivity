using Google.Apis.Calendar.v3.Data;
using Microsoft.Graph.Models;
using OpenTail.Connectivity.Inbox.Gmail;
using OpenTail.Connectivity.Inbox.Graph;

namespace OpenTail.Connectivity.Tests.Inbox;

/// <summary>Times must not depend on the machine's zone; the expected instants below are fixed UTC values.</summary>
public sealed class CalendarTimeTests
{
    [Theory]
    [InlineData("2026-10-20T09:00:00.0000000", "UTC", "2026-10-20T09:00:00+00:00")]
    [InlineData("2026-10-20T09:00:00.0000000", null, "2026-10-20T09:00:00+00:00")]
    [InlineData("2026-10-20T09:00:00.0000000", "America/Los_Angeles", "2026-10-20T16:00:00+00:00")]
    [InlineData("2026-10-20T09:00:00.0000000", "Pacific Standard Time", "2026-10-20T16:00:00+00:00")]
    [InlineData("2026-10-20T09:00:00", "Europe/Berlin", "2026-10-20T07:00:00+00:00")]
    [InlineData("2026-10-20T09:00:00+02:00", "America/Los_Angeles", "2026-10-20T07:00:00+00:00")] // an explicit offset wins
    [InlineData("2026-10-20T09:00:00Z", "Europe/Berlin", "2026-10-20T09:00:00+00:00")]
    [InlineData("2026-10-20T09:00:00", "Not/AZone", "2026-10-20T09:00:00+00:00")] // unknown zone reads as UTC
    public void Zoned_wall_clock_times_name_one_instant(string dateTime, string? zone, string expected)
        => Assert.Equal(DateTimeOffset.Parse(expected), CalendarTime.FromZoned(dateTime, zone));

    [Fact]
    public void Daylight_saving_gaps_and_repeats_use_the_offset_before_the_change()
    {
        // Berlin, 2026-03-29: 02:00 jumps to 03:00. 2026-10-25: 03:00 falls back to 02:00.
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T01:30:00Z"), CalendarTime.FromZoned("2026-03-29T02:30:00", "Europe/Berlin"));
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30:00Z"), CalendarTime.FromZoned("2026-10-25T02:30:00", "Europe/Berlin"));
    }

    [Fact]
    public void Dates_are_midnight_in_their_zone()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T22:00:00Z"), CalendarTime.FromDate("2026-10-20", "Europe/Berlin"));
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T00:00:00Z"), CalendarTime.FromDate("2026-10-20", null));
        Assert.Null(CalendarTime.FromDate("20/10/2026", null));
        Assert.Null(CalendarTime.FromZoned("", "UTC"));
    }

    [Fact]
    public void Writes_render_utc_wall_clock_and_zone_dates()
    {
        var instant = DateTimeOffset.Parse("2026-10-20T09:00:00+02:00");
        Assert.Equal("2026-10-20T07:00:00.0000000", CalendarTime.ToUtcWallClock(instant));
        Assert.Equal("2026-10-20", CalendarTime.ToDate(DateTimeOffset.Parse("2026-10-19T22:00:00Z"), "Europe/Berlin"));
        Assert.Equal("2026-10-20T00:00:00.0000000", CalendarTime.ToWallClock(DateTimeOffset.Parse("2026-10-19T22:00:00Z"), "Europe/Berlin"));
    }

    [Fact]
    public void Graph_events_read_their_zone_and_skip_missing_times()
    {
        var ev = new Microsoft.Graph.Models.Event
        {
            Id = "e1",
            Start = new DateTimeTimeZone { DateTime = "2026-10-20T09:00:00.0000000", TimeZone = "Pacific Standard Time" },
            End = new DateTimeTimeZone { DateTime = "2026-10-20T10:00:00.0000000", TimeZone = "Pacific Standard Time" },
            OriginalStartTimeZone = "Pacific Standard Time",
        };

        var mapped = GraphCalendarMapper.MapEvent(ev, "", "tenant")!;

        Assert.Equal(DateTimeOffset.Parse("2026-10-20T16:00:00Z"), mapped.Start);
        Assert.Equal("Pacific Standard Time", mapped.TimeZone);
        Assert.Null(GraphCalendarMapper.MapEvent(new Microsoft.Graph.Models.Event { Id = "e2" }, "", "tenant"));
    }

    [Fact]
    public void Gmail_all_day_events_use_the_calendar_zone_and_missing_times_are_skipped()
    {
        var allDay = new Google.Apis.Calendar.v3.Data.Event
        {
            Id = "a1",
            Start = new EventDateTime { Date = "2026-10-20" },
            End = new EventDateTime { Date = "2026-10-21" },
        };

        var mapped = GmailCalendarMapper.MapEvent(allDay, "primary", "tenant", calendarTimeZone: "Europe/Berlin")!;

        Assert.True(mapped.IsAllDay);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T22:00:00Z"), mapped.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T22:00:00Z"), mapped.End);
        Assert.Null(GmailCalendarMapper.MapEvent(new Google.Apis.Calendar.v3.Data.Event { Id = "x" }, "primary", "tenant"));
    }
}
