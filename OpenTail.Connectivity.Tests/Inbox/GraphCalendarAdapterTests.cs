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
        var handler = new StubGraph(_ => """{"value":[{"id":"e1"}]}""");
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
        var handler = new StubGraph(_ => """{"id":"e1"}""");
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
        var handler = new StubGraph(_ => """{"id":"e1"}""");
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

        Assert.True(GraphCalendarMapper.MapEvent(occurrence, "", "tenant").IsRecurring);
        Assert.False(GraphCalendarMapper.MapEvent(single, "", "tenant").IsRecurring);
        Assert.True(GraphCalendarMapper.MapEvent(master, "", "tenant").IsRecurring);
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
            ? """{"value":[{"id":"e3"}]}"""
            : """{"value":[{"id":"e1"},{"id":"e2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/me%40example.com/calendar/calendarView?page=2"}""");
        var adapter = Create(handler);

        var events = await adapter.GetEventsAsync(new CalendarFetchOptions(MaxEvents: 10)).ToListAsync();

        Assert.Equal(["e1", "e2", "e3"], events.Select(ev => ev.EventId));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetEvents_stops_paging_when_MaxEvents_is_reached()
    {
        var handler = new StubGraph(uri => uri.Query.Contains("page=2", StringComparison.OrdinalIgnoreCase)
            ? """{"value":[{"id":"e3"}]}"""
            : """{"value":[{"id":"e1"},{"id":"e2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/me%40example.com/calendar/calendarView?page=2"}""");
        var adapter = Create(handler);

        var events = await adapter.GetEventsAsync(new CalendarFetchOptions(MaxEvents: 2)).ToListAsync();

        Assert.Equal(["e1", "e2"], events.Select(ev => ev.EventId));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetEvents_follows_pages_for_an_explicit_calendar()
    {
        var handler = new StubGraph(uri => uri.Query.Contains("page=2", StringComparison.OrdinalIgnoreCase)
            ? """{"value":[{"id":"e2"}]}"""
            : """{"value":[{"id":"e1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/me%40example.com/calendars/abc/calendarView?page=2"}""");
        var adapter = Create(handler);

        var events = await adapter.GetEventsAsync(new CalendarFetchOptions(CalendarId: "abc", MaxEvents: 10)).ToListAsync();

        Assert.Equal(["e1", "e2"], events.Select(ev => ev.EventId));
        Assert.All(events, ev => Assert.Equal("abc", ev.CalendarId));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static GraphCalendarAdapter Create(StubGraph handler)
    {
        var client = new GraphServiceClient(new HttpClient(handler), new AnonymousAuthenticationProvider());
        return new GraphCalendarAdapter(Settings, client);
    }

    private static CalendarEvent Sample(string calendarId)
        => new("e1", calendarId, "subject", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), "UTC",
            null, null, false, false, null, "busy", "normal", "notResponded", false, null, "",
            [], [], "tenant");

    private sealed class StubGraph(Func<Uri, string> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var body = respond(uri);
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Delete ? null : new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }
}
