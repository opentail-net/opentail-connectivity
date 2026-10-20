using Microsoft.Graph.Users.Item.Calendars.Item.Events.Item.Accept;
using Microsoft.Graph.Users.Item.Calendars.Item.Events.Item.Decline;
using Microsoft.Graph.Users.Item.Calendars.Item.Events.Item.TentativelyAccept;

namespace OpenTail.Connectivity.Inbox.Graph;

public sealed class GraphCalendarAdapter : ICalendarAdapter
{
    private readonly GraphSettings _settings;
    private GraphServiceClient? _client;

    public GraphCalendarAdapter(GraphSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    internal GraphCalendarAdapter(GraphSettings settings, GraphServiceClient client)
        : this(settings)
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
        var calendarId = IsDefault(options.CalendarId) ? string.Empty : options.CalendarId!;
        var start = (options.Start ?? DateTimeOffset.UtcNow).ToString("o");
        var end = (options.End ?? DateTimeOffset.UtcNow.AddDays(30)).ToString("o");

        var user = GetClient().Users[_settings.UserEmail];
        var response = IsDefault(options.CalendarId)
            ? await user.Calendar.CalendarView.GetAsync(req =>
            {
                req.QueryParameters.StartDateTime = start;
                req.QueryParameters.EndDateTime = end;
                req.QueryParameters.Top = options.MaxEvents;
                req.QueryParameters.Select =
                [
                    "id", "subject", "start", "end", "location", "body",
                    "isAllDay", "isOnlineMeeting", "onlineMeetingUrl",
                    "showAs", "sensitivity", "responseStatus", "recurrence",
                    "organizer", "attendees", "categories"
                ];
            }, ct)
            : await user.Calendars[calendarId].CalendarView.GetAsync(req =>
            {
                req.QueryParameters.StartDateTime = start;
                req.QueryParameters.EndDateTime = end;
                req.QueryParameters.Top = options.MaxEvents;
                req.QueryParameters.Select =
                [
                    "id", "subject", "start", "end", "location", "body",
                    "isAllDay", "isOnlineMeeting", "onlineMeetingUrl",
                    "showAs", "sensitivity", "responseStatus", "recurrence",
                    "organizer", "attendees", "categories"
                ];
            }, ct);

        foreach (var ev in response?.Value ?? [])
            if (ev is not null)
                // The default-calendar route has no calendar ID segment.
                yield return GraphCalendarMapper.MapEvent(ev, calendarId, _settings.UserEmail);
    }

    public async Task<CalendarEvent?> GetEventAsync(
        string calendarId, string eventId, CancellationToken ct = default)
    {
        var user = GetClient().Users[_settings.UserEmail];
        var ev = IsDefault(calendarId)
            ? await user.Calendar.Events[eventId].GetAsync(cancellationToken: ct)
            : await user.Calendars[calendarId].Events[eventId].GetAsync(cancellationToken: ct);

        return ev is null ? null
            : GraphCalendarMapper.MapEvent(ev, IsDefault(calendarId) ? string.Empty : calendarId, _settings.UserEmail);
    }

    public async Task<string> CreateEventAsync(CalendarEvent draft, CancellationToken ct = default)
    {
        var user = GetClient().Users[_settings.UserEmail];
        var created = IsDefault(draft.CalendarId)
            ? await user.Calendar.Events.PostAsync(ToGraphEvent(draft), cancellationToken: ct)
            : await user.Calendars[draft.CalendarId].Events.PostAsync(ToGraphEvent(draft), cancellationToken: ct);
        return created?.Id ?? string.Empty;
    }

    public async Task UpdateEventAsync(CalendarEvent updated, CancellationToken ct = default)
    {
        var user = GetClient().Users[_settings.UserEmail];
        if (IsDefault(updated.CalendarId))
            await user.Calendar.Events[updated.EventId].PatchAsync(ToGraphEvent(updated), cancellationToken: ct);
        else
            await user.Calendars[updated.CalendarId].Events[updated.EventId].PatchAsync(ToGraphEvent(updated), cancellationToken: ct);
    }

    public async Task DeleteEventAsync(
        string calendarId, string eventId, CancellationToken ct = default)
    {
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

    private GraphServiceClient GetClient()
    {
        if (_client is not null) return _client;
        var credential = new ClientSecretCredential(
            _settings.TenantId, _settings.ClientId, _settings.ClientSecret);
        _client = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        return _client;
    }

    private static Event ToGraphEvent(CalendarEvent e)
        => new()
        {
            Subject = e.Subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = e.Body },
            Start = new DateTimeTimeZone { DateTime = e.Start.ToString("o"), TimeZone = e.TimeZone },
            End = new DateTimeTimeZone { DateTime = e.End.ToString("o"), TimeZone = e.TimeZone },
            Location = string.IsNullOrWhiteSpace(e.Location)
                ? null : new Location { DisplayName = e.Location },
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
}
