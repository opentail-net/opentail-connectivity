namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>A change a calendar adapter is asked to make.</summary>
public enum CalendarWriteKind
{
    Create,
    Update,
    Delete,
    /// <summary>Accept, decline or tentatively accept an invitation. The organiser is told: this leaves the calendar.</summary>
    Respond,
}

/// <param name="SendsExternalResponse">True when the provider tells someone else (the organiser of an invitation).</param>
public sealed record CalendarWriteRequest(
    CalendarWriteKind Kind,
    string CalendarId,
    string? EventId,
    bool SendsExternalResponse,
    string? Response = null);

/// <summary>
/// Which calendar writes an adapter may make. Every write is checked before any provider call; the default,
/// <see cref="Disabled"/>, refuses all of them, so a write-capable adapter is read-only until the caller opts in.
/// </summary>
/// <param name="Approve">Asked after the kind is allowed, for example to get a person's approval; false refuses the write.</param>
public sealed record CalendarWritePolicy(
    bool AllowCreate = false,
    bool AllowUpdate = false,
    bool AllowDelete = false,
    bool AllowRespond = false,
    Func<CalendarWriteRequest, CancellationToken, Task<bool>>? Approve = null)
{
    public static CalendarWritePolicy Disabled { get; } = new();

    /// <summary>Every write, with no approval step. For callers that make their own decisions, not for agents.</summary>
    public static CalendarWritePolicy AllowAll { get; } = new(true, true, true, true);

    public bool Allows(CalendarWriteKind kind) => kind switch
    {
        CalendarWriteKind.Create => AllowCreate,
        CalendarWriteKind.Update => AllowUpdate,
        CalendarWriteKind.Delete => AllowDelete,
        CalendarWriteKind.Respond => AllowRespond,
        _ => false,
    };

    /// <summary>Throws <see cref="CalendarWriteDeniedException"/> unless the policy allows and approves the request.</summary>
    public async Task EnsureAllowedAsync(CalendarWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Allows(request.Kind))
            throw new CalendarWriteDeniedException(request, $"Calendar {request.Kind} is not allowed by the write policy.");
        if (Approve is not null && !await Approve(request, cancellationToken).ConfigureAwait(false))
            throw new CalendarWriteDeniedException(request, $"Calendar {request.Kind} was not approved.");
    }
}

/// <summary>A calendar write refused by <see cref="CalendarWritePolicy"/>. Nothing was sent to the provider.</summary>
public sealed class CalendarWriteDeniedException(CalendarWriteRequest request, string message) : InvalidOperationException(message)
{
    public CalendarWriteRequest Request { get; } = request;
}
