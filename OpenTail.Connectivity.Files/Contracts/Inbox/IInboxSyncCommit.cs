namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>When an inbox adapter with a sync state store saves its state.</summary>
public enum InboxSyncCommit
{
    /// <summary>
    /// When the caller has enumerated every message, before it knows whether they were stored: a message that fails
    /// downstream is not fetched again. Only for callers that store each message durably while enumerating.
    /// </summary>
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

/// <summary>Shared argument check for the inbox adapters.</summary>
public static class InboxSyncCommits
{
    /// <summary>
    /// With a sync store the caller must choose when state is saved: there is no default, because
    /// <see cref="InboxSyncCommit.AfterEnumeration"/> can lose messages a downstream store fails to write.
    /// </summary>
    public static InboxSyncCommit Require(bool hasSyncStore, InboxSyncCommit? commit)
    {
        if (hasSyncStore && commit is null)
            throw new ArgumentException(
                "Choose InboxSyncCommit.Manual (save after your store has flushed, then call CommitSyncStateAsync) or " +
                "InboxSyncCommit.AfterEnumeration when passing a sync state store.", nameof(commit));
        return commit ?? InboxSyncCommit.Manual;
    }
}
