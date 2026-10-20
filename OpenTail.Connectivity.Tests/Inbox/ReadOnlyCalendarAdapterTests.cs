using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Inbox;
using OpenTail.Connectivity.Files.Models;
using System.Runtime.CompilerServices;

namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class ReadOnlyCalendarAdapterTests
{
    [Fact]
    public async Task Reads_are_forwarded_to_the_inner_adapter()
    {
        var inner = new FakeCalendarAdapter();
        var adapter = new ReadOnlyCalendarAdapter(inner);

        Assert.Equal([inner.Calendar], await adapter.ListCalendarsAsync().ToListAsync());
        Assert.Equal([inner.Event], await adapter.GetEventsAsync(new CalendarFetchOptions()).ToListAsync());
        Assert.Equal(inner.Event, await adapter.GetEventAsync("cal", "event"));
        Assert.Equal(3, inner.ReadCalls);
    }

    [Fact]
    public async Task Writes_throw_without_calling_the_inner_adapter()
    {
        var inner = new FakeCalendarAdapter();
        var adapter = new ReadOnlyCalendarAdapter(inner);

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.CreateEventAsync(inner.Event));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.UpdateEventAsync(inner.Event));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.DeleteEventAsync("cal", "event"));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.RespondToEventAsync("cal", "event", "accepted"));

        Assert.Equal(0, inner.WriteCalls);
    }

    private sealed class FakeCalendarAdapter : ICalendarAdapter
    {
        public CalendarInfo Calendar { get; } = new("cal", "Calendar", true, "tenant");
        public CalendarEvent Event { get; } = new("event", "cal", "subject", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1), "UTC", null, null, false, false, null, "busy", "normal",
            "notResponded", false, null, "", [], [], "tenant");
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }

        public async IAsyncEnumerable<CalendarInfo> ListCalendarsAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            ReadCalls++;
            yield return Calendar;
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<CalendarEvent> GetEventsAsync(CalendarFetchOptions options, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ReadCalls++;
            yield return Event;
            await Task.CompletedTask;
        }

        public Task<CalendarEvent?> GetEventAsync(string calendarId, string eventId, CancellationToken ct = default)
        {
            ReadCalls++;
            return Task.FromResult<CalendarEvent?>(Event);
        }

        public Task<string> CreateEventAsync(CalendarEvent draft, CancellationToken ct = default)
        {
            WriteCalls++;
            return Task.FromResult(Event.EventId);
        }

        public Task UpdateEventAsync(CalendarEvent updated, CancellationToken ct = default)
        {
            WriteCalls++;
            return Task.CompletedTask;
        }

        public Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default)
        {
            WriteCalls++;
            return Task.CompletedTask;
        }

        public Task RespondToEventAsync(string calendarId, string eventId, string response, string? comment = null, CancellationToken ct = default)
        {
            WriteCalls++;
            return Task.CompletedTask;
        }
    }
}
