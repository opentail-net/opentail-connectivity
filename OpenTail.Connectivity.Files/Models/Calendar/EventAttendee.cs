namespace OpenTail.Connectivity.Files.Models;

public sealed record EventAttendee(
    string Email,
    string? DisplayName,
    string Type,            // required | optional | resource
    string ResponseStatus); // accepted | tentative | declined | notResponded
