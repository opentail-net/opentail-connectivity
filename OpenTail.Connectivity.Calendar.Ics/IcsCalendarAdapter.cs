using System.Globalization;

namespace OpenTail.Connectivity.Calendar.Ics;

/// <summary>
/// Read-only adapter for ICS feeds or CalDAV calendar URLs.
/// Mutating methods throw <see cref="NotSupportedException"/> — ICS feeds are read-only.
/// </summary>
/// <remarks>
/// With a time window, <see cref="GetEventsAsync"/> returns occurrences: recurring events are expanded inside the window
/// (EXDATE and overrides applied). A window with one side open is closed one year from the given side. With no window, it
/// returns each event once as stored, so a recurring event appears as its series. At most <c>MaxEvents</c> are returned,
/// earliest first.
/// </remarks>
public sealed class IcsCalendarAdapter : ICalendarAdapter
{
    private readonly string _icsUrl;
    private readonly string _calendarId;
    private readonly string _tenantContext;
    private readonly HttpClient _http;
    private readonly TimeSpan _cacheTtl;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private string? _cachedIcs;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

    public IcsCalendarAdapter(
        string icsUrl,
        string calendarId,
        string tenantContext,
        HttpClient http,
        TimeSpan? cacheTtl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icsUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        ArgumentNullException.ThrowIfNull(http);
        _icsUrl = icsUrl;
        _calendarId = calendarId;
        _tenantContext = tenantContext;
        _http = http;
        _cacheTtl = cacheTtl ?? TimeSpan.FromSeconds(30);
    }

    public async IAsyncEnumerable<CalendarInfo> ListCalendarsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new CalendarInfo(
            CalendarId: _calendarId,
            Name: _calendarId,
            IsDefault: false,
            TenantContext: _tenantContext);
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<CalendarEvent> GetEventsAsync(
        CalendarFetchOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxEvents, 1, nameof(options.MaxEvents));
        if (options.CalendarId is not null && options.CalendarId != _calendarId)
            yield break;

        var ics = await GetIcsAsync(ct);
        IEnumerable<CalendarEvent> events;
        if (options.Start is null && options.End is null)
        {
            events = IcsSerializer.Deserialize(ics, _tenantContext).OrderBy(e => e.Start);
        }
        else
        {
            var from = options.Start ?? options.End!.Value.AddYears(-1);
            var to = options.End ?? options.Start!.Value.AddYears(1);
            events = IcsSerializer.Occurrences(ics, _tenantContext, from, to);
        }

        foreach (var ev in events.Take(options.MaxEvents))
            yield return ev with { CalendarId = _calendarId };
    }

    public async Task<CalendarEvent?> GetEventAsync(
        string calendarId, string eventId, CancellationToken ct = default)
    {
        if (calendarId != _calendarId)
            return null;
        var ics = await GetIcsAsync(ct);
        var ev = IcsSerializer.Deserialize(ics, _tenantContext).FirstOrDefault(e => e.EventId == eventId);
        if (ev is null && OccurrenceStart(eventId) is { } start)
        {
            // An occurrence id (uid_yyyyMMddTHHmmssZ) from GetEventsAsync.
            ev = IcsSerializer.Occurrences(ics, _tenantContext, start, start).FirstOrDefault(e => e.EventId == eventId);
        }
        return ev is null ? null : ev with { CalendarId = _calendarId };
    }

    private static DateTimeOffset? OccurrenceStart(string eventId)
    {
        var cut = eventId.LastIndexOf('_');
        return cut > 0 && DateTimeOffset.TryParseExact(eventId[(cut + 1)..], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)
            ? start
            : null;
    }

    private async Task<string> GetIcsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedIcs is not null && now < _cacheExpiry)
            return _cachedIcs;

        await _cacheLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock in case another caller refreshed.
            now = DateTimeOffset.UtcNow;
            if (_cachedIcs is not null && now < _cacheExpiry)
                return _cachedIcs;

            _cachedIcs = await _http.GetStringAsync(_icsUrl, ct);
            _cacheExpiry = now.Add(_cacheTtl);
            return _cachedIcs;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public Task<string> CreateEventAsync(CalendarEvent draft, CancellationToken ct = default)
        => throw new NotSupportedException("ICS calendar adapters are read-only.");

    public Task UpdateEventAsync(CalendarEvent updated, CancellationToken ct = default)
        => throw new NotSupportedException("ICS calendar adapters are read-only.");

    public Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default)
        => throw new NotSupportedException("ICS calendar adapters are read-only.");

    public Task RespondToEventAsync(
        string calendarId, string eventId,
        string response, string? comment = null,
        CancellationToken ct = default)
        => throw new NotSupportedException("ICS calendar adapters are read-only.");
}
