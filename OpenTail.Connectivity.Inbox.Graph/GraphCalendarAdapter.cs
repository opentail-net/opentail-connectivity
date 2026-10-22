using Microsoft.Graph.Users.Item.Calendars.Item.Events.Item.Accept;
using Microsoft.Graph.Users.Item.Calendars.Item.Events.Item.Decline;
using Microsoft.Graph.Users.Item.Calendars.Item.Events.Item.TentativelyAccept;

namespace OpenTail.Connectivity.Inbox.Graph;

public sealed class GraphCalendarAdapter : ICalendarAdapter
{
    private readonly GraphSettings _settings;
    private readonly CalendarWritePolicy _writePolicy;
    private GraphServiceClient? _client;

    /// <param name="writePolicy">Which writes are allowed. Default <see cref="CalendarWritePolicy.Disabled"/>: read-only.</param>
    public GraphCalendarAdapter(GraphSettings settings, CalendarWritePolicy? writePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _writePolicy = writePolicy ?? CalendarWritePolicy.Disabled;
    }

    internal GraphCalendarAdapter(GraphSettings settings, GraphServiceClient client, CalendarWritePolicy? writePolicy = null)
        : this(settings, writePolicy)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async IAsyncEnumerable<CalendarInfo> ListCalendarsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await GetClient()
            .Users[_settings.UserEmail]
            .Calendars
            .GetAsync(cancellationToken: ct);

        foreach (var cal in response?.Value ?? [])
            if (cal is not null)
                yield return GraphCalendarMapper.MapCalendar(cal, _settings.UserEmail);
    }

    public async IAsyncEnumerable<CalendarEvent> GetEventsAsync(
        CalendarFetchOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (options.MaxEvents <= 0)
            yield break;

        var calendarId = IsDefault(options.CalendarId) ? string.Empty : options.CalendarId!;
        var start = (options.Start ?? DateTimeOffset.UtcNow).ToString("o");
        var end = (options.End ?? DateTimeOffset.UtcNow.AddDays(30)).ToString("o");

        var user = GetClient().Users[_settings.UserEmail];
        var yielded = 0;
        if (IsDefault(options.CalendarId))
        {
            var calendarView = user.Calendar.CalendarView;
            var response = await calendarView.GetAsync(req =>
            {
                req.QueryParameters.StartDateTime = start;
                req.QueryParameters.EndDateTime = end;
                req.QueryParameters.Top = options.MaxEvents;
                req.QueryParameters.Select =
                [
                    "id", "subject", "start", "end", "location", "body",
                    "isAllDay", "isOnlineMeeting", "onlineMeetingUrl",
                    "showAs", "sensitivity", "responseStatus", "recurrence",
                    "organizer", "attendees", "categories", "type", "seriesMasterId", "originalStartTimeZone"
                ];
                req.Headers.Add("Prefer", PreferUtc);
            }, ct);

            while (response is not null)
            {
                foreach (var ev in response.Value ?? [])
                    if (ev is not null && GraphCalendarMapper.MapEvent(ev, calendarId, _settings.UserEmail) is { } mapped)
                    {
                        yield return mapped;
                        if (++yielded >= options.MaxEvents)
                            yield break;
                    }

                if (string.IsNullOrEmpty(response.OdataNextLink))
                    yield break;
                response = await calendarView.WithUrl(response.OdataNextLink).GetAsync(req => req.Headers.Add("Prefer", PreferUtc), ct);
            }
        }
        else
        {
            var calendarView = user.Calendars[calendarId].CalendarView;
            var response = await calendarView.GetAsync(req =>
            {
                req.QueryParameters.StartDateTime = start;
                req.QueryParameters.EndDateTime = end;
                req.QueryParameters.Top = options.MaxEvents;
                req.QueryParameters.Select =
                [
                    "id", "subject", "start", "end", "location", "body",
                    "isAllDay", "isOnlineMeeting", "onlineMeetingUrl",
                    "showAs", "sensitivity", "responseStatus", "recurrence",
                    "organizer", "attendees", "categories", "type", "seriesMasterId", "originalStartTimeZone"
                ];
                req.Headers.Add("Prefer", PreferUtc);
            }, ct);

            while (response is not null)
            {
                foreach (var ev in response.Value ?? [])
                    if (ev is not null && GraphCalendarMapper.MapEvent(ev, calendarId, _settings.UserEmail) is { } mapped)
                    {
                        yield return mapped;
                        if (++yielded >= options.MaxEvents)
                            yield break;
                    }

                if (string.IsNullOrEmpty(response.OdataNextLink))
                    yield break;
                response = await calendarView.WithUrl(response.OdataNextLink).GetAsync(req => req.Headers.Add("Prefer", PreferUtc), ct);
            }
        }
    }

    public async Task<CalendarEvent?> GetEventAsync(
        string calendarId, string eventId, CancellationToken ct = default)
    {
        var user = GetClient().Users[_settings.UserEmail];
        var ev = IsDefault(calendarId)
            ? await user.Calendar.Events[eventId].GetAsync(req => req.Headers.Add("Prefer", PreferUtc), ct)
            : await user.Calendars[calendarId].Events[eventId].GetAsync(req => req.Headers.Add("Prefer", PreferUtc), ct);

        return ev is null ? null
            : GraphCalendarMapper.MapEvent(ev, IsDefault(calendarId) ? string.Empty : calendarId, _settings.UserEmail);
    }

    public async Task<string> CreateEventAsync(CalendarEvent draft, CancellationToken ct = default)
    {
        await _writePolicy.EnsureAllowedAsync(new(CalendarWriteKind.Create, draft.CalendarId, null, false), ct);
        var user = GetClient().Users[_settings.UserEmail];
        var created = IsDefault(draft.CalendarId)
            ? await user.Calendar.Events.PostAsync(ToGraphEvent(draft), cancellationToken: ct)
            : await user.Calendars[draft.CalendarId].Events.PostAsync(ToGraphEvent(draft), cancellationToken: ct);
        return created?.Id ?? string.Empty;
    }

    public async Task UpdateEventAsync(CalendarEvent updated, CancellationToken ct = default)
    {
        await _writePolicy.EnsureAllowedAsync(new(CalendarWriteKind.Update, updated.CalendarId, updated.EventId, false), ct);
        var user = GetClient().Users[_settings.UserEmail];
        if (IsDefault(updated.CalendarId))
            await user.Calendar.Events[updated.EventId].PatchAsync(ToGraphEvent(updated), cancellationToken: ct);
        else
            await user.Calendars[updated.CalendarId].Events[updated.EventId].PatchAsync(ToGraphEvent(updated), cancellationToken: ct);
    }

    public async Task DeleteEventAsync(
        string calendarId, string eventId, CancellationToken ct = default)
    {
        await _writePolicy.EnsureAllowedAsync(new(CalendarWriteKind.Delete, calendarId, eventId, false), ct);
        var user = GetClient().Users[_settings.UserEmail];
        if (IsDefault(calendarId))
            await user.Calendar.Events[eventId].DeleteAsync(cancellationToken: ct);
        else
            await user.Calendars[calendarId].Events[eventId].DeleteAsync(cancellationToken: ct);
    }

    public async Task RespondToEventAsync(
        string calendarId, string eventId,
        string response, string? comment = null,
        CancellationToken ct = default)
    {
        // Graph sends the response to the organiser (SendResponse = true).
        await _writePolicy.EnsureAllowedAsync(new(CalendarWriteKind.Respond, calendarId, eventId, true, response), ct);
        var user = GetClient().Users[_settings.UserEmail];

        switch (response.ToLowerInvariant())
        {
            case "accepted":
                if (IsDefault(calendarId))
                    await user.Calendar.Events[eventId].Accept.PostAsync(
                        new Microsoft.Graph.Users.Item.Calendar.Events.Item.Accept.AcceptPostRequestBody { Comment = comment, SendResponse = true },
                        cancellationToken: ct);
                else
                    await user.Calendars[calendarId].Events[eventId].Accept.PostAsync(
                        new AcceptPostRequestBody { Comment = comment, SendResponse = true },
                        cancellationToken: ct);
                break;
            case "tentative":
                if (IsDefault(calendarId))
                    await user.Calendar.Events[eventId].TentativelyAccept.PostAsync(
                        new Microsoft.Graph.Users.Item.Calendar.Events.Item.TentativelyAccept.TentativelyAcceptPostRequestBody { Comment = comment, SendResponse = true },
                        cancellationToken: ct);
                else
                    await user.Calendars[calendarId].Events[eventId].TentativelyAccept.PostAsync(
                        new TentativelyAcceptPostRequestBody { Comment = comment, SendResponse = true },
                        cancellationToken: ct);
                break;
            case "declined":
                if (IsDefault(calendarId))
                    await user.Calendar.Events[eventId].Decline.PostAsync(
                        new Microsoft.Graph.Users.Item.Calendar.Events.Item.Decline.DeclinePostRequestBody { Comment = comment, SendResponse = true },
                        cancellationToken: ct);
                else
                    await user.Calendars[calendarId].Events[eventId].Decline.PostAsync(
                        new DeclinePostRequestBody { Comment = comment, SendResponse = true },
                        cancellationToken: ct);
                break;
            default:
                throw new ArgumentException($"Unknown response type '{response}'", nameof(response));
        }
    }

    private static bool IsDefault(string? calendarId)
        => string.IsNullOrWhiteSpace(calendarId) || calendarId == "calendar";

    /// <summary>Asks Graph for times in UTC, so reading them never depends on the mailbox's own zone setting.</summary>
    private const string PreferUtc = "outlook.timezone=\"UTC\"";

    private GraphServiceClient GetClient()
    {
        if (_client is not null) return _client;
        var credential = new ClientSecretCredential(
            _settings.TenantId, _settings.ClientId, _settings.ClientSecret);
        _client = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        return _client;
    }

    // Graph reads dateTime as a wall clock in timeZone, so no offset goes with it. Events go in their own zone, so a recurring
    // series keeps its local time across daylight-saving changes (UTC would move it an hour), and all-day events stay midnight.
    // Graph takes Windows zone ids; an IANA id is converted. Only an unknown zone falls back to UTC.
    private static DateTimeTimeZone ToGraphTime(DateTimeOffset value, CalendarEvent e)
        => CalendarTime.ToWallClock(value, e.TimeZone) is { } local
            ? new DateTimeTimeZone { DateTime = local, TimeZone = GraphZoneId(e.TimeZone) }
            : new DateTimeTimeZone { DateTime = CalendarTime.ToUtcWallClock(value), TimeZone = "UTC" };

    private static string GraphZoneId(string zone)
        => TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Trim(), out var windows) ? windows : zone.Trim();

    private static Event ToGraphEvent(CalendarEvent e)
    {
        var ev = new Event
        {
            Subject = e.Subject,
            Start = ToGraphTime(e.Start, e),
            End = ToGraphTime(e.End, e),
            IsAllDay = e.IsAllDay,
            Attendees = e.Attendees
                .Select(a => new Attendee
                {
                    EmailAddress = new EmailAddress { Address = a.Email, Name = a.DisplayName },
                    Type = a.Type switch
                    {
                        "optional" => AttendeeType.Optional,
                        "resource" => AttendeeType.Resource,
                        _ => AttendeeType.Required
                    }
                })
                .ToList()
        };

        // The SDK sends any property that was assigned, even as null, and a PATCH then clears it. So body and location are set only
        // when the model has them: an empty body would wipe the event's text, Teams join details included. HTML, because events
        // read through Graph come back as HTML, so a round trip keeps the join block.
        if (e.Body is not null)
            ev.Body = new ItemBody { ContentType = BodyType.Html, Content = e.Body };
        if (!string.IsNullOrWhiteSpace(e.Location))
            ev.Location = new Location { DisplayName = e.Location };
        return ev;
    }
}
