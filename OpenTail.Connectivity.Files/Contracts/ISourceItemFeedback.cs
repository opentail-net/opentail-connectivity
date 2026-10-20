namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>
/// Implemented by source connectors that keep sync state, so the pipeline can say which discovered items it did not turn into
/// documents (extraction failed, validation failed, an error, or the run stopped at its item cap). The connector then leaves
/// them out of the state it saves, and the next run offers them again.
/// </summary>
public interface ISourceItemFeedback
{
    void NotProcessed(SourceItem item);
}
