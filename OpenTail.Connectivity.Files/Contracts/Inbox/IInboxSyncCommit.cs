namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>When an inbox adapter with a sync state store saves its state.</summary>
public enum InboxSyncCommit
{
    /// <summary>When the caller has enumerated every message (the default, and the behaviour before this option existed).</summary>
    AfterEnumeration,

    /// <summary>
    /// Only when the caller calls <see cref="IInboxSyncCommit.CommitSyncStateAsync"/>, after it has stored or exported what it
    /// read. A run that fails before that fetches the same messages again next time, rather than losing them.
    /// </summary>
    Manual,
}

/// <summary>Implemented by inbox adapters that keep sync state, for callers that commit it themselves.</summary>
public interface IInboxSyncCommit
{
    /// <summary>A completed enumeration's state is waiting to be saved.</summary>
    bool HasPendingSyncState { get; }

    /// <summary>Saves the state of the last completed enumeration, if any. Does nothing when nothing is pending.</summary>
    Task CommitSyncStateAsync(CancellationToken cancellationToken = default);
}
