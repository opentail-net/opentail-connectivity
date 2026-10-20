namespace OpenTail.Connectivity.Files.Contracts;

public sealed record CalendarFetchOptions(
    string? CalendarId = null,
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null,
    int MaxEvents = 50);
