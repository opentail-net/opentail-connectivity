using System.Net;
using Microsoft.Graph;
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
