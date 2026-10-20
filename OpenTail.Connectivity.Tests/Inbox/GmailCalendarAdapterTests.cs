using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using System.IO.Compression;

namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class GmailCalendarAdapterTests
{
    private static readonly GmailSettings Settings = GmailSettings.Create("client", "secret", "refresh", "me@example.com");

    [Fact]
    public async Task RespondToEvent_preserves_description_and_sets_attendee_comment()
    {
        string? updateBody = null;
        using var handler = new StubCalendarHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"id":"event-1","description":"original description","attendees":[{"email":"me@example.com","responseStatus":"needsAction"}]}""");
            var bytes = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (request.Content.Headers.ContentEncoding.Contains("gzip"))
            {
                using var compressed = new MemoryStream(bytes);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var expanded = new MemoryStream();
                gzip.CopyTo(expanded);
                bytes = expanded.ToArray();
            }
            updateBody = Encoding.UTF8.GetString(bytes);
            return Json("{}");
        });
        using var service = CreateService(handler);
        var adapter = new GmailCalendarAdapter(Settings, service);

        await adapter.RespondToEventAsync("primary", "event-1", "accepted", "I will attend");

        using var document = JsonDocument.Parse(updateBody!);
        var updated = document.RootElement;
        Assert.Equal("original description", updated.GetProperty("description").GetString());
        Assert.Equal("accepted", updated.GetProperty("attendees")[0].GetProperty("responseStatus").GetString());
        Assert.Equal("I will attend", updated.GetProperty("attendees")[0].GetProperty("comment").GetString());
    }

    [Fact]
    public async Task RespondToEvent_rejects_unknown_responses_without_sending_an_update()
    {
        var requests = 0;
        using var handler = new StubCalendarHandler(_ =>
        {
            requests++;
            return Json("{}");
        });
        using var service = CreateService(handler);
        var adapter = new GmailCalendarAdapter(Settings, service);

        await Assert.ThrowsAsync<ArgumentException>(() => adapter.RespondToEventAsync("primary", "event-1", "maybe"));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task UpdateEvent_patches_only_the_model_fields_and_keeps_attendee_answers()
    {
        HttpMethod? method = null;
        string? body = null;
        using var handler = new StubCalendarHandler(request =>
        {
            method = request.Method;
            var bytes = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (request.Content.Headers.ContentEncoding.Contains("gzip"))
            {
                using var compressed = new MemoryStream(bytes);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var expanded = new MemoryStream();
                gzip.CopyTo(expanded);
                bytes = expanded.ToArray();
            }
            body = Encoding.UTF8.GetString(bytes);
            return Json("{}");
        });
        using var service = CreateService(handler);
        var adapter = new GmailCalendarAdapter(Settings, service);
        var updated = new CalendarEvent("event-1", "primary", "New subject", DateTimeOffset.Parse("2026-10-20T09:00:00Z"),
            DateTimeOffset.Parse("2026-10-20T10:00:00Z"), "UTC", null, null, false, false, null, "busy", "normal", "accepted", true, null,
            "me@example.com", [new EventAttendee("guest@example.com", null, "required", "accepted")], [], "tenant");

        await adapter.UpdateEventAsync(updated);

        Assert.Equal(new HttpMethod("PATCH"), method);
        using var document = JsonDocument.Parse(body!);
        var sent = document.RootElement;
        Assert.Equal("New subject", sent.GetProperty("summary").GetString());
        Assert.False(sent.TryGetProperty("recurrence", out _));
        Assert.False(sent.TryGetProperty("reminders", out _));
        Assert.False(sent.TryGetProperty("conferenceData", out _));
        Assert.False(sent.TryGetProperty("description", out _)); // null: left unchanged
        Assert.Equal("accepted", sent.GetProperty("attendees")[0].GetProperty("responseStatus").GetString());
    }

    [Fact]
    public async Task GetEvents_stops_at_MaxEvents_across_pages()
    {
        var requests = new List<string>();
        using var handler = new StubCalendarHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            requests.Add(url);
            return Json(url.Contains("pageToken=two", StringComparison.Ordinal)
                ? EventsPage(["3", "4"], "three")
                : url.Contains("pageToken=three", StringComparison.Ordinal)
                    ? EventsPage(["5"])
                    : EventsPage(["1", "2"], "two"));
        });
        using var service = CreateService(handler);
        var adapter = new GmailCalendarAdapter(Settings, service);

        var events = new List<CalendarEvent>();
        await foreach (var ev in adapter.GetEventsAsync(new CalendarFetchOptions(MaxEvents: 3)))
            events.Add(ev);

        Assert.Equal(["1", "2", "3"], events.Select(ev => ev.EventId));
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task GetEvent_uses_calendar_zone_for_all_day_events_without_an_event_zone()
    {
        var requests = new List<Uri>();
        using var handler = new StubCalendarHandler(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri);
            if (uri.AbsolutePath.EndsWith("/events/event-1", StringComparison.Ordinal))
                return Json(AllDayEvent());
            if (uri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
                return Json($"{{\"timeZone\":\"Europe/Berlin\",\"items\":[{AllDayEvent()}]}}");
            if (uri.AbsolutePath.EndsWith("/calendars/primary", StringComparison.Ordinal))
                return Json("{\"id\":\"primary\",\"timeZone\":\"Europe/Berlin\"}");
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        using var service = CreateService(handler);
        var adapter = new GmailCalendarAdapter(Settings, service);

        var single = await adapter.GetEventAsync("primary", "event-1");
        var listed = Assert.Single(await adapter.GetEventsAsync(new CalendarFetchOptions()).ToListAsync());

        Assert.NotNull(single);
        Assert.Equal(listed.Start, single.Start);
        Assert.Equal(listed.End, single.End);
        Assert.Contains(requests, uri => uri.AbsolutePath.EndsWith("/calendars/primary", StringComparison.Ordinal));
    }

    private static string AllDayEvent()
        => "{\"id\":\"event-1\",\"summary\":\"All day\",\"start\":{\"date\":\"2026-10-20\"},\"end\":{\"date\":\"2026-10-21\"}}";

    private static CalendarService CreateService(HttpMessageHandler handler)
        => new(new BaseClientService.Initializer
        {
            HttpClientInitializer = GoogleCredential.FromAccessToken("test-token"),
            HttpClientFactory = new StubCalendarHttpClientFactory(handler),
            ApplicationName = "OpenTail-Tests"
        });

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string EventsPage(string[] ids, string? nextPageToken = null)
        => JsonSerializer.Serialize(new
        {
            items = ids.Select(id => new { id, summary = $"event {id}", start = new { dateTime = "2026-10-20T09:00:00Z" }, end = new { dateTime = "2026-10-20T10:00:00Z" } }),
            nextPageToken
        });

    private sealed class StubCalendarHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
            => new(new ConfigurableMessageHandler(handler));
    }

    private sealed class StubCalendarHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
