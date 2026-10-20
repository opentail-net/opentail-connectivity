namespace OpenTail.Connectivity.Inbox.Gmail;

/// <summary>
/// Centralised Gmail OAuth scope definitions. All credential builders in this project
/// reference these constants so scopes never drift between inbox, send, and calendar flows.
/// </summary>
public static class GmailScopes
{
    /// <summary>All scopes needed for inbox read, send, modify, and Google Calendar.</summary>
    public static readonly string[] Full =
    [
        GmailService.Scope.GmailReadonly,
        GmailService.Scope.GmailSend,
        GmailService.Scope.GmailModify,
        Google.Apis.Calendar.v3.CalendarService.Scope.Calendar
    ];

    /// <summary>Read-only scopes — no send or calendar.</summary>
    public static readonly string[] ReadOnly =
    [
        GmailService.Scope.GmailReadonly
    ];

    /// <summary>
    /// Send and modify, no calendar. Despite the name this can read mail: <c>gmail.modify</c> includes reading messages. For a
    /// token that cannot read, use <see cref="SendWithoutRead"/>.
    /// </summary>
    public static readonly string[] SendOnly =
    [
        GmailService.Scope.GmailSend,
        GmailService.Scope.GmailModify
    ];

    /// <summary>Send only (<c>gmail.send</c>): no reading, labelling or calendar.</summary>
    public static readonly string[] SendWithoutRead =
    [
        GmailService.Scope.GmailSend
    ];
}
