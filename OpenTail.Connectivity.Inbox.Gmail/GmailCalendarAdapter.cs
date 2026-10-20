using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using GEvent = Google.Apis.Calendar.v3.Data.Event;

namespace OpenTail.Connectivity.Inbox.Gmail;

public sealed class GmailCalendarAdapter : ICalendarAdapter
{
    private readonly GmailSettings _settings;
    private readonly GmailCredentialProvider _credentialProvider;

    public GmailCalendarAdapter(GmailSettings settings, GmailCredentialProvider? credentialProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _credentialProvider = credentialProvider ?? new GmailCredentialProvider(settings);
    }

    public async IAsyncEnumerable<CalendarInfo> ListCalendarsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var svc = await BuildServiceAsync(ct);
        var list = await svc.CalendarList.List().ExecuteAsync(ct);
        foreach (var cal in list?.Items ?? [])
            if (cal is not null)
                yield return GmailCalendarMapper.MapCalendar(cal, _settings.UserEmail);
    }

    public async IAsyncEnumerable<CalendarEvent> GetEventsAsync(
        CalendarFetchOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var svc = await BuildServiceAsync(ct);
        var calendarId = options.CalendarId ?? "primary";

        var request = svc.Events.List(calendarId);
        request.TimeMinDateTimeOffset = options.Start ?? DateTimeOffset.UtcNow;
        request.TimeMaxDateTimeOffset = options.End ?? DateTimeOffset.UtcNow.AddDays(30);
        request.MaxResults = options.MaxEvents;
        request.SingleEvents = true;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        Events? response;
        do
        {
            response = await request.ExecuteAsync(ct);
            foreach (var ev in response?.Items ?? [])
                if (ev is not null)
                    yield return GmailCalendarMapper.MapEvent(ev, calendarId, _settings.UserEmail);
            request.PageToken = response?.NextPageToken;
        }
        while (!string.IsNullOrEmpty(request.PageToken));
    }

    public async Task<CalendarEvent?> GetEventAsync(
        string calendarId, string eventId, CancellationToken ct = default)
    {
        var svc = await BuildServiceAsync(ct);
        var ev = await svc.Events.Get(calendarId, eventId).ExecuteAsync(ct);
        return ev is null ? null
            : GmailCalendarMapper.MapEvent(ev, calendarId, _settings.UserEmail);
    }

    public async Task<string> CreateEventAsync(CalendarEvent draft, CancellationToken ct = default)
    {
        var svc = await BuildServiceAsync(ct);
        var ev = ToGoogleEvent(draft);
        var created = await svc.Events
            .Insert(ev, draft.CalendarId)
            .ExecuteAsync(ct);
        return created?.Id ?? string.Empty;
    }

    public async Task UpdateEventAsync(CalendarEvent updated, CancellationToken ct = default)
    {
        var svc = await BuildServiceAsync(ct);
        await svc.Events
            .Update(ToGoogleEvent(updated), updated.CalendarId, updated.EventId)
            .ExecuteAsync(ct);
    }

    public async Task DeleteEventAsync(
        string calendarId, string eventId, CancellationToken ct = default)
    {
        var svc = await BuildServiceAsync(ct);
        await svc.Events.Delete(calendarId, eventId).ExecuteAsync(ct);
    }

    public async Task RespondToEventAsync(
        string calendarId, string eventId,
        string response, string? comment = null,
        CancellationToken ct = default)
    {
        var svc = await BuildServiceAsync(ct);
        var ev = await svc.Events.Get(calendarId, eventId).ExecuteAsync(ct);
        if (ev?.Attendees is null) return;

        var self = ev.Attendees.FirstOrDefault(
            a => string.Equals(a.Email, _settings.UserEmail, StringComparison.OrdinalIgnoreCase));
        if (self is null) return;

        self.ResponseStatus = response.ToLowerInvariant() switch
        {
            "accepted" => "accepted",
            "tentative" => "tentative",
            "declined" => "declined",
            _ => self.ResponseStatus
        };
        if (!string.IsNullOrWhiteSpace(comment))
            ev.Description = comment;

        await svc.Events.Update(ev, calendarId, eventId).ExecuteAsync(ct);
    }

    private async Task<CalendarService> BuildServiceAsync(CancellationToken ct)
    {
        var credential = await BuildCredentialAsync(ct);
        return new CalendarService(new Google.Apis.Services.BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "OpenTail-Connectivity"
        });
    }

    private Task<Google.Apis.Auth.OAuth2.ICredential> BuildCredentialAsync(CancellationToken ct)
        => _credentialProvider.GetCredentialAsync(ct);

    private static GEvent ToGoogleEvent(CalendarEvent e)
        => new()
        {
            Summary = e.Subject,
            Description = e.Body,
            Location = e.Location,
            Start = new EventDateTime
            {
                DateTimeRaw = e.Start.ToString("o"),
                TimeZone = e.TimeZone
            },
            End = new EventDateTime
            {
                DateTimeRaw = e.End.ToString("o"),
                TimeZone = e.TimeZone
            },
            Attendees = e.Attendees
                .Select(a => new Google.Apis.Calendar.v3.Data.EventAttendee
                {
                    Email = a.Email,
                    DisplayName = a.DisplayName,
                    Optional = a.Type == "optional"
                })
                .ToList()
        };
}
