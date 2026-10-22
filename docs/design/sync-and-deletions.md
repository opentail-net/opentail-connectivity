# Design: incremental sync and deletions

**Status:** built (2026-10-20). The sync core (`IIncrementalSourceConnector`, checkpoints, flush-before-commit, `ISyncCursorStore`,
`FileSyncCursorStore`), deletions (`SourceChange.Delete`, `IDeletionExporter`, `JsonlDeletionExporter`), the retry list and
full-run comparison (`SeenSourceRefs`) are all implemented. See `docs/done/ingestion-hardening.md` for implementation work
packages. The first change-feed connector is `OpenTail.Connectivity.Drive.Graph` (Graph delta). 

**Implementation note:** Retries use `FetchAsync` directly rather than the `FindAsync` method shown in the contract below, so
`FindAsync` is not part of the actual interface. The sections below are the original design for reference.

**Where it fits:** the sync behind tiers 2 and 3 of [document search](../roadmap.md#document-search-in-three-tiers). Build it in
steps: the smaller core (checkpoints, saving only after the exporters flush, the version check) with the first tier 2 or 3
connector, and deletions and retries with the first connector that has a change feed.

## The problem today

- **Delivery is at most once.** ~~`FolderSyncStateStore` records every scanned file, including ones that failed, so a failure is
  not retried until the file changes.~~ **Fixed:** the folder connector implements `ISourceItemFeedback`; the pipeline calls
  `NotProcessed` for failed items, which keeps them out of the saved state so the next run retries them. ~~The inbox adapters save their cursor when enumeration ends, before the caller has processed
  anything.~~ **Fixed:** `InboxSyncCommit.Manual` exists (plan section 10).
- **There is no notion of success.** Sync state is saved by the connector (inbox) or by the caller (`SaveStateAsync`) without
  knowing whether the run worked.
- **A connector has no way to report a deletion.** `ISourceConnector` yields `SourceItem`s only. The Graph and Drive designs need
  to pass on deletions their change feeds report, and had invented a report field for it.
- ~~**Long first runs cannot resume.**~~ **Fixed:** A crash or a `MaxItemsProcessed` cap at item 90,000 of 100,000 can now resume from the last checkpoint (folder failure issue fixed in plan section 10).

## Goals

1. **At least once.** The cursor never moves past an item that has not been exported, or recorded as needing a retry.
2. **The pipeline commits, not the connector.** Only the pipeline knows whether items were exported.
3. **Deletions travel as data**, from the connector through the pipeline to the exporters, in order with the changes.
4. **Resumable.** Long runs commit progress as they go.
5. **Fail closed.** Anything unclear means "do not commit" or "refuse to start", never "assume it worked".
6. **Additive.** `ISourceConnector` and today's connectors are unchanged. A connector opts in by implementing a second interface.

## Overview

```
                 load state                                    save state (after flush)
ISyncCursorStore ----------> pipeline ----------------------------------------------> ISyncCursorStore
                                 |                                        ^
                                 | GetChangesAsync(cursor)                | at each Checkpoint
                                 v                                        |
              IIncrementalSourceConnector --> Upsert / Delete / Checkpoint / Reset
                                                 |        |
                                   fetch, extract,        +--> IDeletionExporter.DeleteAsync
                                   validate, export
```

The connector streams changes since a cursor. Every so often it yields a **checkpoint**: a cursor that is valid once
everything before it has been handled. The pipeline handles each change, and at a checkpoint it flushes the exporters and then
saves the cursor. If the run stops anywhere, the next run resumes from the last saved checkpoint and repeats what came after it.

## Contracts

### The connector

```csharp
namespace OpenTail.Connectivity.Files.Contracts;

public interface IIncrementalSourceConnector : ISourceConnector
{
    /// What this connector reads, stable across runs: "graph-drive:acme:b!xyz", "sql:postgres:db1/public.tickets".
    string SyncScope { get; }

    /// True if the change stream can contain Delete changes. The pipeline refuses to start if it can and nothing exports them.
    bool ReportsDeletions { get; }

    /// Changes since the cursor; a null cursor means from the beginning (a full run).
    IAsyncEnumerable<SourceChange> GetChangesAsync(string? cursor, CancellationToken ct = default);

    // NOTE: FindAsync shown below was not implemented. Retries use FetchAsync from ISourceConnector instead.
    // /// Looks one item up again, for retries. Returns null only when the source confirms it no longer exists;
    // /// throws on any other failure.
    // Task<SourceItem?> FindAsync(string sourceRef, CancellationToken ct = default);
}

public abstract record SourceChange
{
    private SourceChange() { }

    public sealed record Upsert(SourceItem Item) : SourceChange;
    public sealed record Delete(string SourceRef, string TenantContext, DeletionReason Reason = DeletionReason.Deleted) : SourceChange;
    public sealed record Checkpoint(string Cursor) : SourceChange;
    public sealed record Reset(string Reason) : SourceChange;
}

public enum DeletionReason { Deleted, OutOfScope }
```

`FetchAsync` from `ISourceConnector` still fetches an upserted item's bytes. `DiscoverAsync` stays for callers that do not use a
cursor store; a connector can implement it as the `Upsert` items of `GetChangesAsync(null)`.

**What a connector promises.**
- A `Checkpoint` covers every change yielded before it. Resuming from that cursor must not skip anything after it.
- The cursor is opaque to everyone else and may be compound (for example JSON holding a page token and a pending watermark).
- `Reset` means the stored cursor was rejected (expired delta token, schema change) and a full listing follows. The connector
  yields it before that listing.
- `OutOfScope` covers items that still exist but left what the connector reads, such as a file moved out of the synced folder or
  a permission removed. Consumers handle it as a deletion.
- An exception from `GetChangesAsync` means the stream is broken. The pipeline commits nothing more and rethrows.

### The store

```csharp
public interface ISyncCursorStore
{
    Task<SyncState?> LoadAsync(string scope, CancellationToken ct = default);

    /// Fails with SyncConflictException if the stored version is not expectedVersion (another run committed meanwhile).
    Task SaveAsync(string scope, SyncState state, long expectedVersion, CancellationToken ct = default);

    Task ResetAsync(string scope, CancellationToken ct = default);
}

public sealed record SyncState(
    string? Cursor,
    IReadOnlyList<RetryEntry> Retries,
    long Version,
    DateTimeOffset UpdatedAt);

public sealed record RetryEntry(string SourceRef, int Attempts, string LastError, DateTimeOffset FirstFailedAt);

// FileSyncCursorStore(path): one JSON file for many scopes, written to a temp file and moved into place.
```

Compared with the earlier sketch, the state carries the retry list and a version. The version stops two runs on one scope from
both committing and moving the cursor backwards: the second run fails at its first save. A corrupt file is treated as no state
(a full run), as `FolderSyncStateStore` does today, and the report says so.

### The exporter

```csharp
public interface IDeletionExporter
{
    Task DeleteAsync(DocumentDeletion deletion, CancellationToken ct = default);
}

public sealed record DocumentDeletion(
    string Id,              // the stable id from shared-groundwork section 1
    string SourceRef,
    string SourceType,
    string TenantContext,
    DeletionReason Reason,
    DateTimeOffset DetectedAt);
```

An exporter implements this alongside `IConnectivityExporter` if it can apply deletions. A deletion for an id it does not hold
is a no-op, not an error: connectors may report items as `OutOfScope` without knowing whether they were ever exported. `JsonlDocumentExporter` gains an opt-in
(see open questions) and a separate `JsonlDeletionExporter` writes one deletion per line.

`FlushAsync` becomes "may be called several times per run": once at each committed checkpoint and once at the end. The two built-in
exporters already allow that; the contract change is stated for third-party exporters.

### Pipeline options and report

```csharp
// PipelineOptions gains:
ISyncCursorStore? SyncStore = null,
SyncOptions? Sync = null

public sealed record SyncOptions(
    int CheckpointEveryItems = 500,      // commit at the first checkpoint after this many items...
    TimeSpan? CheckpointEvery = null,    // ...or after this long (default 5 minutes), whichever comes first
    int MaxItemAttempts = 3,        // historical design; built as SyncOptions.MaxItemAttempts, default 5
    int MaxRetryEntries = 10_000,   // historical design; built as SyncOptions.MaxRetryItems, default 100
    bool IgnoreDeletions = false,        // allow a deletion-reporting connector with no deletion exporter
    bool CollectSourceRefs = false,
    int MaxReportedRefs = 10_000);

// IngestionReport gains:
string? SyncScope, bool FullRun, bool SyncCommitted,
int ItemsDeleted, int ItemsRetried, int ItemsAbandoned, int ItemsRejected,   // historical; built: ItemsDeleted, RetriesPending
IReadOnlyList<string>? DeletedSourceRefs, IReadOnlyList<string>? SeenSourceRefs, bool SourceRefsTruncated
```

Both records are positional, so the new parameters go last with defaults. Source code keeps compiling; already-built consumers
need rebuilding, which every release requires anyway because all packages share one version.

## How the pipeline runs an incremental connector

It takes this path when the connector implements `IIncrementalSourceConnector` and `PipelineOptions.SyncStore` is set. Otherwise
it behaves exactly as today.

1. **Before reading.** If `ReportsDeletions` is true, no exporter implements `IDeletionExporter` and `IgnoreDeletions` is false,
   throw `InvalidOperationException` before any call to the source. Otherwise load the state for `SyncScope`.
2. **Retries first.** For each retry entry, call `FindAsync`. An item found is processed like an upsert. `null` becomes a
   `Delete`. An exception adds one to the entry's attempts; at `MaxItemAttempts` the entry is dropped and counted as abandoned,
   with its ref and last error in `Errors`.
3. **The stream.** Call `GetChangesAsync(state.Cursor)` and handle each change in order:
   - `Upsert`: fetch, extract, normalise, validate, export, as today. An upsert removes any retry entry for the same ref.
   - `Delete`: build a `DocumentDeletion` and send it to every `IDeletionExporter`. It also removes any retry entry for the ref.
   - `Checkpoint`: remember it. When `CheckpointEveryItems` or `CheckpointEvery` has passed, commit (step 5).
   - `Reset`: mark the report `FullRun`, record the reason as a warning, and clear the retry list.
4. **Item outcomes.** Each upsert ends in exactly one of these:

   | Outcome | When | Retried? |
   |---|---|---|
   | Exported | All exporters accepted it. | No. |
   | Skipped | Empty text, or a duplicate hash. | No. |
   | Rejected | Extraction failed, or validation found an error. The same bytes would fail again; a change to the item brings it back through the stream. | No. Counted in `ItemsRejected`. |
   | Retry | `FetchAsync` threw (network, throttling, a timeout). | Yes: added to the retry list. |

   An exporter that throws is not an item problem: it usually means the store is down. In an incremental run it stops the run,
   nothing past the last commit is saved, and the exception is rethrown. Non-incremental runs keep today's behaviour of counting
   the item as failed.
5. **Commit.** Call `FlushAsync` on every exporter, then `SaveAsync(scope, new SyncState(checkpoint, retries, ...), version)`.
   Never save before the flush has succeeded. (Built: when the retry list is full, at `MaxRetryItems`, the cursor is held rather than the run failed.) If the retry list is over `MaxRetryEntries`, do not save; fail the run with a clear
   error, since that many fetch failures means something systemic.
6. **End.** When the stream ends, commit at its last checkpoint. A stream that ends without a final checkpoint commits only the
   retry list, keeping the old cursor.
7. **Stopping early.** On an exception, cancellation or the `MaxItemsProcessed` cap, the pipeline stops and saves nothing more.
   Everything after the last commit is repeated next run. Exceptions and cancellation propagate as today; the cap returns a report
   with `SyncCommitted` showing whether anything was saved.

A crash between the flush and the save repeats the items since the previous commit. With stable ids those repeats overwrite the
same documents, so they are harmless.

## Deletions

There are two ways a consumer learns that something is gone, and the design keeps them separate.

**Reported deletions.** Connectors whose source has a change feed (Graph delta, Drive changes, Zendesk incremental export) yield
`Delete`. These go to deletion exporters as they happen and are counted in the report, with the refs listed up to
`MaxReportedRefs`.

**Full-run comparison.** Connectors without a feed (SQL, object storage) cannot see deletions in an incremental run. With
`CollectSourceRefs`, a run that started from no cursor (or hit a `Reset`) and reached the end of the stream fills `SeenSourceRefs`
with every upserted ref, **whatever its outcome**. A rejected or retried item still exists at the source, so it must not look
deleted. The consumer removes what it holds and the list does not contain, for that scope and tenant. `SeenSourceRefs` is null
for incremental runs, partial runs and runs resumed from a checkpoint, so a consumer cannot mistake them for a full listing. If the
list is over `MaxReportedRefs` it is truncated, `SourceRefsTruncated` is true, and the consumer must not use it for deletions.

**Deletion rule (the same on every page):** delete by absence only after a run that was full and completed, whose `SeenSourceRefs` is not null and whose `SourceRefsTruncated` is false. `SourceRefsTruncated` is true when the list hit `MaxReportedRefs` (warning `SOURCE_REFS_TRUNCATED`) or when some items' refs are unknown this run, for example an archive that could not be expanded (warning `SOURCE_REFS_INCOMPLETE`); either way the list must not be used for deletions. A failed or retried item is still in the list, because it still exists at the source. Never infer deletions from absence in an incremental run.
Large sources need an exporter that records seen refs rather than the report.

The pipeline never infers a deletion from absence.

## What each planned connector would emit

| Connector | Cursor | Checkpoints | Deletions |
|---|---|---|---|
| Graph files | The page's `@odata.nextLink`, then the final `@odata.deltaLink`. | After each page. | `Delete` for items with the `deleted` facet; `OutOfScope` for files that fail the filter. `Reset` on HTTP 410 (resync). |
| Google Drive | JSON with a phase: a file listing (with the start token taken before it), then the changes feed's page tokens. | After each page. | `Delete` for `removed` changes and trashed files; `OutOfScope` for files that fail the filter. `Reset` if a token is rejected. |
| SQL | `(modified, id)` of the last row in a batch. | After each keyset batch. | None (`ReportsDeletions = false`). Full runs with `CollectSourceRefs`. |
| Object storage | JSON `{ watermark, maxSeen, resumeToken, lastKey }`. | After each listing page; the watermark advances only at the end, because listings are in key order, not time order. | None. Full runs with `CollectSourceRefs`. |

## Existing connectors

- **Folder.** Out of scope for the first version. It keeps `FolderSyncStateStore`, which holds a timestamp per file and is a poor
  fit for one opaque cursor. Two small fixes are worth doing separately: let `SaveStateAsync` take the report's failed refs and
  leave them out of the state, so they are retried; and, later, yield `Delete` for files in the state that a complete walk did not
  find.
- **Inbox adapters.** They are not `ISourceConnector`s and save their own state, so this design does not reach them. Their fix
  belongs in a separate change: save state only when the caller says the records were processed.
- **Csv, Email, Web, Social.** No change. They have no cursor and keep running as full runs.

## Testing

All of this can be tested without a real service:
- A scripted fake connector that yields a given sequence of upserts, deletes, checkpoints and resets, and can throw at a chosen
  point.
- Fake exporters that fail on a chosen document or on flush, and record flush and delete calls.
- A fake store that records saves, enforces versions and can be made to "crash" after a flush.

Cases: the cursor is never saved before the flush; a crash after any change repeats exactly the changes since the last commit;
retries are attempted, cleared by a later upsert or delete, and abandoned after `MaxItemAttempts`; a fetch failure retries but an
extraction failure does not; an exporter failure stops the run without saving; a deletion-reporting connector with no deletion
exporter refuses to start; a `Reset` clears retries and marks the run full; `SeenSourceRefs` includes rejected items and is null
for partial runs; a concurrent run fails with `SyncConflictException`; `MaxItemsProcessed` commits only the last checkpoint
reached.

## Order and size

1. Contracts, `FileSyncCursorStore` and the pipeline path, tested with fakes (medium). Needs stable ids (shared groundwork
   section 1) first, because `DocumentDeletion.Id` uses them.
2. `JsonlDeletionExporter` (small).
3. The first real connector, probably SQL, since its first step needs no new dependency.
4. The folder `SaveStateAsync` fix (small, separate).

## Decisions (provisional)

The maintainers' first answers, to revisit if implementation shows otherwise:

- Stopping an incremental run on an exporter failure is acceptable, although non-incremental runs count it as an item failure.
- `FlushAsync` may be called several times per run.
- Deletions in JSONL go to a separate file (`JsonlDeletionExporter`), so existing readers of the document file see no change.
- `FindAsync` returning null means deletion.
- The defaults stand: commit every 500 items or 5 minutes, 3 retry attempts, at most 10,000 queued retries.
