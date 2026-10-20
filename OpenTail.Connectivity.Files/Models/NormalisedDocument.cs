namespace OpenTail.Connectivity.Files.Models;

public sealed record NormalisedDocument(
    string Id,
    string Title,
    string Body,
    string ContentHash,
    string SourceRef,
    string SourceType,
    string TenantContext,
    DateTimeOffset SourceModified,
    DateTimeOffset IngestedAt,
    IReadOnlyDictionary<string, string> Metadata);
