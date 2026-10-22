using System.Net;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Authentication;
using OpenTail.Connectivity.Inbox.Graph;

namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class GraphCalendarAdapterTests
{
    private static readonly GraphSettings Settings = GraphSettings.Create("tenant", "client", "secret", "me@example.com");

    [Fact]
    public async Task GetEvents_uses_default_calendar_route_and_returns_empty_calendar_id()
    {
        var handler = new StubGraph(_ => """{"value":[{"id":"e1","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}}]}""");
        var adapter = Create(handler);

        var ev = Assert.Single(await adapter.GetEventsAsync(new CalendarFetchOptions()).ToListAsync());

        Assert.Contains("/calendar/calendarView", handler.Requests[0].AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/calendars/calendar", handler.Requests[0].AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, ev.CalendarId);
    }

    [Fact]
    public async Task GetEvents_uses_calendar_id_route_for_explicit_calendar()
    {
        var handler = new StubGraph(_ => """{"value":[]}""");
        var adapter = Create(handler);

        await adapter.GetEventsAsync(new CalendarFetchOptions(CalendarId: "abc")).ToListAsync();

        Assert.Contains("/calendars/abc/calendarView", handler.Requests[0].AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Default_calendar_event_operations_use_default_route()
    {
        var handler = new StubGraph(_ => """{"id":"e1","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}}""");
        var adapter = Create(handler);

        await adapter.GetEventAsync(string.Empty, "e1");
        await adapter.CreateEventAsync(Sample(string.Empty));
        await adapter.UpdateEventAsync(Sample(string.Empty));
        await adapter.DeleteEventAsync(string.Empty, "e1");
        await adapter.RespondToEventAsync(string.Empty, "e1", "accepted");

        Assert.Contains(handler.Requests, uri => uri.AbsolutePath.EndsWith("/calendar/events/e1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, uri => uri.AbsolutePath.EndsWith("/calendar/events", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, uri => uri.AbsolutePath.EndsWith("/calendar/events/e1/accept", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.Contains("/calendars/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Explicit_calendar_event_operations_keep_calendar_id_route()
    {
        var handler = new StubGraph(_ => """{"id":"e1","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}}""");
        var adapter = Create(handler);

        await adapter.GetEventAsync("abc", "e1");
        await adapter.CreateEventAsync(Sample("abc"));
        await adapter.UpdateEventAsync(Sample("abc"));
        await adapter.DeleteEventAsync("abc", "e1");
        await adapter.RespondToEventAsync("abc", "e1", "accepted");

        Assert.Contains(handler.Requests, uri => uri.AbsolutePath.EndsWith("/calendars/abc/events", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, uri => uri.AbsolutePath.Contains("/calendars/abc/events/e1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, uri => uri.AbsolutePath.EndsWith("/calendars/abc/events/e1/accept", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Recurrence_type_classifies_occurrences_and_series_masters()
    {
        var occurrence = new Event { Type = EventType.Occurrence };
        var single = new Event { Type = EventType.SingleInstance };
        var master = new Event { Type = EventType.SeriesMaster };

        Assert.True(GraphCalendarMapper.MapEvent(Timed(occurrence), "", "tenant")!.IsRecurring);
        Assert.False(GraphCalendarMapper.MapEvent(Timed(single), "", "tenant")!.IsRecurring);
        Assert.True(GraphCalendarMapper.MapEvent(Timed(master), "", "tenant")!.IsRecurring);
    }

    [Fact]
    public async Task Calendar_view_selects_event_type()
    {
        var handler = new StubGraph(_ => """{"value":[]}""");
        var adapter = Create(handler);

        await adapter.GetEventsAsync(new CalendarFetchOptions()).ToListAsync();

        var query = Uri.UnescapeDataString(handler.Requests[0].Query);
        Assert.Contains("type", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("seriesMasterId", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetEvents_follows_calendar_view_pages()
    {
        var handler = new StubGraph(uri => uri.Query.Contains("page=2", StringComparison.OrdinalIgnoreCase)
            ? """{"value":[{"id":"e3","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}}]}"""
            : """{"value":[{"id":"e1","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}},{"id":"e2","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/me%40example.com/calendar/calendarView?page=2"}""");
        var adapter = Create(handler);

        var events = await adapter.GetEventsAsync(new CalendarFetchOptions(MaxEvents: 10)).ToListAsync();

        Assert.Equal(["e1", "e2", "e3"], events.Select(ev => ev.EventId));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetEvents_stops_paging_when_MaxEvents_is_reached()
    {
        var handler = new StubGraph(uri => uri.Query.Contains("page=2", StringComparison.OrdinalIgnoreCase)
            ? """{"value":[{"id":"e3","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}}]}"""
            : """{"value":[{"id":"e1","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}},{"id":"e2","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/me%40example.com/calendar/calendarView?page=2"}""");
        var adapter = Create(handler);

        var events = await adapter.GetEventsAsync(new CalendarFetchOptions(MaxEvents: 2)).ToListAsync();

        Assert.Equal(["e1", "e2"], events.Select(ev => ev.EventId));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetEvents_follows_pages_for_an_explicit_calendar()
    {
        var handler = new StubGraph(uri => uri.Query.Contains("page=2", StringComparison.OrdinalIgnoreCase)
            ? """{"value":[{"id":"e2","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}}]}"""
            : """{"value":[{"id":"e1","start":{"dateTime":"2026-10-20T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-20T10:00:00","timeZone":"UTC"}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/me%40example.com/calendars/abc/calendarView?page=2"}""");
        var adapter = Create(handler);

        var events = await adapter.GetEventsAsync(new CalendarFetchOptions(CalendarId: "abc", MaxEvents: 10)).ToListAsync();

        Assert.Equal(["e1", "e2"], events.Select(ev => ev.EventId));
        Assert.All(events, ev => Assert.Equal("abc", ev.CalendarId));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static GraphCalendarAdapter Create(StubGraph handler, CalendarWritePolicy? policy = null)
    {
        var client = new GraphServiceClient(new HttpClient(handler), new AnonymousAuthenticationProvider());
        return new GraphCalendarAdapter(Settings, client, policy ?? CalendarWritePolicy.AllowAll);
    }

    [Fact]
    public async Task Writes_are_refused_by_default_before_any_request()
    {
        var handler = new StubGraph(_ => "{}");
        var client = new GraphServiceClient(new HttpClient(handler), new AnonymousAuthenticationProvider());
        var adapter = new GraphCalendarAdapter(Settings, client);
        var draft = Sample("");

        await Assert.ThrowsAsync<CalendarWriteDeniedException>(() => adapter.CreateEventAsync(draft));
        await Assert.ThrowsAsync<CalendarWriteDeniedException>(() => adapter.UpdateEventAsync(draft));
        await Assert.ThrowsAsync<CalendarWriteDeniedException>(() => adapter.DeleteEventAsync("", "e1"));
        var respond = await Assert.ThrowsAsync<CalendarWriteDeniedException>(() => adapter.RespondToEventAsync("", "e1", "accepted"));

        Assert.True(respond.Request.SendsExternalResponse);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_allowed_respond_still_needs_approval_when_the_policy_asks()
    {
        var handler = new StubGraph(_ => "{}");
        var asked = new List<CalendarWriteRequest>();
        var adapter = Create(handler, new CalendarWritePolicy(AllowRespond: true, Approve: (r, _) => { asked.Add(r); return Task.FromResult(false); }));

        await Assert.ThrowsAsync<CalendarWriteDeniedException>(() => adapter.RespondToEventAsync("", "e1", "declined"));

        Assert.Equal(CalendarWriteKind.Respond, Assert.Single(asked).Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Reads_ask_for_utc_and_writes_send_the_events_own_wall_clock_and_zone()
    {
        var handler = new StubGraph(_ => """{"value":[],"id":"e1"}""");
        var adapter = Create(handler);
        var timed = Sample("abc") with { Start = DateTimeOffset.Parse("2026-10-20T09:00:00+02:00"), End = DateTimeOffset.Parse("2026-10-20T10:00:00+02:00"), TimeZone = "Europe/Berlin" };

        await adapter.GetEventsAsync(new CalendarFetchOptions(CalendarId: "abc")).ToListAsync();
        await adapter.CreateEventAsync(timed);
        await adapter.CreateEventAsync(timed with { IsAllDay = true, Start = DateTimeOffset.Parse("2026-10-19T22:00:00Z"), End = DateTimeOffset.Parse("2026-10-20T22:00:00Z") });

        Assert.Contains(handler.Prefers, p => p.Contains("outlook.timezone=\"UTC\"", StringComparison.Ordinal));
        using var timedBody = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("2026-10-20T09:00:00.0000000", timedBody.RootElement.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal("W. Europe Standard Time", timedBody.RootElement.GetProperty("start").GetProperty("timeZone").GetString());
        using var allDayBody = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("2026-10-20T00:00:00.0000000", allDayBody.RootElement.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal("W. Europe Standard Time", allDayBody.RootElement.GetProperty("start").GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task Writes_fall_back_to_utc_for_an_unknown_zone_and_send_a_body_only_when_there_is_one()
    {
        var handler = new StubGraph(_ => """{"id":"e1"}""");
        var adapter = Create(handler);
        var unknown = Sample("abc") with { Start = DateTimeOffset.Parse("2026-10-20T09:00:00+02:00"), End = DateTimeOffset.Parse("2026-10-20T10:00:00+02:00"), TimeZone = "Not/AZone" };

        await adapter.UpdateEventAsync(unknown);
        await adapter.UpdateEventAsync(unknown with { Body = "<p>Agenda</p>" });

        using var noBody = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("2026-10-20T07:00:00.0000000", noBody.RootElement.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal("UTC", noBody.RootElement.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.False(noBody.RootElement.TryGetProperty("body", out _));
        Assert.False(noBody.RootElement.TryGetProperty("location", out _));
        using var withBody = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("html", withBody.RootElement.GetProperty("body").GetProperty("contentType").GetString());
    }

    private static CalendarEvent Sample(string calendarId)
        => new("e1", calendarId, "subject", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), "UTC",
            null, null, false, false, null, "busy", "normal", "notResponded", false, null, "",
            [], [], "tenant");

    private sealed class StubGraph(Func<Uri, string> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public List<string> Prefers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (request.Content is not null)
                Bodies.Add(request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            if (request.Headers.TryGetValues("Prefer", out var prefer))
                Prefers.Add(string.Join(",", prefer));
            var body = respond(uri);
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Delete ? null : new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }

    private static Event Timed(Event e)
    {
        e.Start = new DateTimeTimeZone { DateTime = "2026-10-20T09:00:00", TimeZone = "UTC" };
        e.End = new DateTimeTimeZone { DateTime = "2026-10-20T10:00:00", TimeZone = "UTC" };
        return e;
    }
}
