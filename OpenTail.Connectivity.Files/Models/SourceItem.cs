namespace OpenTail.Connectivity.Files.Models;

/// <param name="Metadata">
/// Facts the connector knows about the item, copied into the document's metadata under <c>source.</c> (a key already starting
/// with <c>source.</c> is kept as it is). See <see cref="SourceKeys"/> for the standard keys and <see cref="SourceAcl"/> for
/// permissions.
/// </param>
public sealed record SourceItem(
    string Id,
    string DisplayName,
    string Extension,
    long SizeBytes,
    DateTimeOffset Modified,
    string TenantContext,
    IReadOnlyDictionary<string, string>? Metadata = null);
