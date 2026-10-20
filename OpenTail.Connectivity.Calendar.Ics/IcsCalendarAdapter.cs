namespace OpenTail.Connectivity.Calendar.Ics;

/// <summary>
/// Read-only adapter for ICS feeds or CalDAV calendar URLs.
/// Mutating methods throw <see cref="NotSupportedException"/> — ICS feeds are read-only.
/// </summary>
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
        var ics = await GetIcsAsync(ct);
        var events = IcsSerializer.Deserialize(ics, _tenantContext);

        foreach (var ev in events)
        {
            if (options.Start.HasValue && ev.End < options.Start.Value) continue;
            if (options.End.HasValue && ev.Start > options.End.Value) continue;
            yield return ev with { CalendarId = _calendarId };
        }
    }

    public async Task<CalendarEvent?> GetEventAsync(
        string calendarId, string eventId, CancellationToken ct = default)
    {
        var ics = await GetIcsAsync(ct);
        return IcsSerializer.Deserialize(ics, _tenantContext)
            .FirstOrDefault(e => e.EventId == eventId);
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
