namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>
/// Turns one fetched item whose content holds other files (an archive) into child items. Set as
/// <see cref="Pipeline.PipelineOptions.Expander"/>: the pipeline fetches the container once, exports each child as its own
/// document and never exports the container itself. Works for any source, because it runs after the fetch.
/// </summary>
public interface IItemExpander
{
    /// <summary>Returns an error when a root source id conflicts with this expander's child-id namespace.</summary>
    string? ValidateSourceId(SourceItem item) => null;

    /// <summary>Whether <paramref name="item"/> should be expanded rather than extracted. Decided from the item alone, without content.</summary>
    bool CanExpand(SourceItem item);

    /// <summary>
    /// The children of <paramref name="item"/>. Throws <see cref="InvalidDataException"/> when the container as a whole cannot be
    /// read or breaks a limit; nothing of it is exported then. A single bad entry is refused instead, and the rest go through.
    /// </summary>
    ItemExpansion Expand(SourceItem item, byte[] content, CancellationToken cancellationToken = default);
}

/// <summary>A child item and its content, ready for extraction.</summary>
public sealed record ExpandedItem(SourceItem Item, byte[] Content);

/// <summary>An entry that was not expanded. <paramref name="Path"/> is printable but otherwise as the archive named it: untrusted.</summary>
public sealed record RefusedEntry(string Path, string Reason);

public sealed record ItemExpansion(IReadOnlyList<ExpandedItem> Items, IReadOnlyList<RefusedEntry> Refused);
