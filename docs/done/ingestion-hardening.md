# Plan: ingestion hardening (informed by Rag.NET)

**Status:** built, 2026-10-20 (plan section 26). WP4 was built without `FindAsync`: see its section. Work packages (WP) are sized and written so the smaller coding model can
take them one at a time. The ones marked **(owner model)** need design judgement and stay with the main model.

## Where this comes from

The owner added `C:\Git\split\examples\Rag.NET` (MIT, Marcel Roozekrans, release 1.1.3) and asked ChatGPT what to take from it.
Its advice: borrow Rag.NET's ingestion engineering (incremental sync, deletions, unchanged-item skipping, conformance testing),
not its RAG framework. That advice was checked against both codebases:

| ChatGPT's point | What the code says | Verdict |
|---|---|---|
| Deletions are not propagated | True. `SourceChange` has `Upsert`, `Checkpoint`, `Reset` only (`Files/Contracts/IIncrementalSourceConnector.cs`). `docs/design/sync-and-deletions.md` already designs them. | **Do it** (WP1–WP3) |
| Failed files are recorded as scanned and not retried | Out of date: fixed (`ISourceItemFeedback`, plan section 10). **But** a related problem is real: a failed fetch in an incremental run holds the cursor (`ConnectivityPipeline`, `holdCommits`), so an item that *always* fails stops the cursor at that point on every run. | **Do it**, as the retry list (WP4) |
| Inbox cursors advance before processing finishes | Out of date: `InboxSyncCommit.Manual` exists (plan section 10). | Nothing to do |
| Cursor commits only after export | Already built: the pipeline flushes exporters before saving the cursor. | Nothing to do |
| Skip unchanged items before fetching (ETag) | True, and worth a small contract now. Payoff is limited today: no current connector has a cheap version token except RSS (entry id + updated) and web pages (HTTP `ETag`). The big wins come with Graph files, Google Drive and object storage, which aren't built. | **Small contract + two connectors** (WP6) |
| Connector conformance tests and verification labels | True. `docs/design/testing-and-verification.md` designs both. Memory has a conformance suite (`MemoryBackendConformance`); connectors don't. | **Do it** (WP7, WP8) |
| Sitemap and RSS/Atom | Already built: `SitemapParser`, `RssFeedConnector` (RSS and Atom). Regression gaps remain. | **Tests only** (WP9) |
| Resilient job queue, webhooks | Belongs to the Host's triggers design (plan section 21), not to Connectivity. | **No work here**; references noted for triggers |
| SaaS connectors | Demand-driven. The Host covers Jira and Confluence through profiles. | **No work now**; references noted |
| Don't port the RAG pipeline or `IFileContentProvider` | Agreed. Keep `ISourceConnector`, `ITextExtractor`, `NormalisedDocument` and the exporters. | Rule for every WP |

The stale status lines in the two design docs are fixed in WP0.

## Rules for every work package

These go at the top of every prompt to the coding model:

- Repository `C:\Git\split\opentail-connectivity` (.NET 10, `OpenTail.Connectivity.slnx`, xUnit tests in
  `OpenTail.Connectivity.Tests`). Bash builds need `export GITHUB_PACKAGES_TOKEN=$(gh auth token)`.
- **Keep the existing contracts.** Add types and optional members; don't replace `ISourceConnector`, `IIncrementalSourceConnector`,
  `IConnectivityExporter` or `NormalisedDocument`. New members on public records go last and are optional, so existing calls compile.
- **Rag.NET is a reference, not a source.** Read it, then write OpenTail's own code against OpenTail's contracts. If a block of
  Rag.NET code is adapted closely (more than a few lines), add an entry to `THIRD-PARTY-NOTICES.md` at the repo root (create it
  if missing) with Rag.NET's MIT copyright and licence text, and a comment at the adapted code naming the source file.
- Tests first: each regression test must fail on the current code, then pass. One local commit per WP, with a message starting
  with the WP id (`WP1: ...`). **Do not push.**
- Run only the tests you touched, then `dotnet build OpenTail.Connectivity.slnx` once (0 warnings). Don't run
  `OpenTail.Connectivity.Tests.PgVector`.
- Match the surrounding style: file-scoped namespaces, terse comments that say why, `ConfigureAwait(false)` where the file uses
  it, `TimeProvider` for anything time-based, invariant culture.
- If a WP turns out bigger or different than described, stop, write down what you found, and don't improvise a redesign.
- Report per WP: done or skipped (why), commit hash, tests added, surprises.

## Order and batches

| Batch | WPs | Who | Size | Done |
|---|---|---|---|---|
| A | WP0, WP1, WP2, WP3 | coding model | medium | ✅ |
| B | WP4 | **owner model** | medium | ✅ |
| C | WP7a, WP9 | coding model | medium | ✅ |
| D | WP6, WP7b, WP8 | coding model | medium | ✅ |
| E | WP5 | coding model | small | ✅ |

A before B (WP4 builds on WP1's pipeline path). C can run in parallel with B. D after C (WP7b uses WP7a's suite).

### Checklist

- [x] WP0: bring design docs up to date
- [x] WP1: deletions through the sync core
- [x] WP2: JSONL deletion exporter
- [x] WP3: full-run source refs for deletion by comparison
- [x] WP4: retry list instead of holding the cursor *(owner model)*
- [x] WP5: folder connector reports deletions after a complete walk
- [x] WP6: skip unchanged items by source version
- [x] WP7a: connector conformance suite + first three connectors
- [x] WP7b: conformance for remaining connectors
- [x] WP8: verification labels
- [x] WP9: RSS and sitemap regression tests

---

## WP0. Bring the design docs up to date (docs only, small) ✅

Files: `docs/design/sync-and-deletions.md`, `docs/design/testing-and-verification.md`.
- In `sync-and-deletions.md`, replace the status line ("proposal. Nothing here is implemented") with what's true: the smaller
  sync core is built (`IIncrementalSourceConnector`, checkpoints, flush-before-commit, `ISyncCursorStore`, `FileSyncCursorStore`,
  the SQLite connector as first user). Deletions, retries and full-run comparison are planned in `docs/ingestion-hardening.md`.
  In "The problem today", mark the folder-failure and inbox-cursor bullets as fixed (plan section 10).
- In `testing-and-verification.md`, add one line under the status: conformance and labels are planned as WP7 and WP8 in
  `docs/ingestion-hardening.md`.
- No other edits. Commit `WP0: design docs say what is built`.

## WP1. Deletions through the sync core (medium) ✅

Goal: a connector can report "this item is gone", and the pipeline passes it to exporters that can apply it, in order with
upserts, before the next checkpoint commits.

Contracts (in `OpenTail.Connectivity.Files`, next to the existing ones):
```csharp
// Contracts/IIncrementalSourceConnector.cs, inside SourceChange:
/// <summary>An item no longer exists at the source, or no longer matches the connector's filter.</summary>
public sealed record Delete(string SourceRef, DeletionReason Reason) : SourceChange;

// Models/DocumentDeletion.cs
public enum DeletionReason { Deleted, OutOfScope }

/// <param name="Id">The stable id, ConnectivityPipeline.DocumentId(sourceType, tenant, sourceRef): the same id the upsert had.</param>
public sealed record DocumentDeletion(string Id, string SourceRef, string SourceType, string TenantContext,
    DeletionReason Reason, DateTimeOffset DetectedAt);

// Contracts/IDeletionExporter.cs
/// <summary>An exporter that can remove documents. A deletion for an id it never held is a no-op, not an error.</summary>
public interface IDeletionExporter
{
    Task DeleteAsync(DocumentDeletion deletion, CancellationToken cancellationToken = default);
}
```
- Add `bool ReportsDeletions => false;` as a **default interface member** on `IIncrementalSourceConnector`, so existing
  connectors (SQLite) compile unchanged.

Pipeline (`Pipeline/ConnectivityPipeline.cs`, the incremental loop around the `switch (change)`):
- `case SourceChange.Delete d:` honours the item cap the same way as `Upsert` (stop before it if the cap is reached). Build the
  `DocumentDeletion` with the same `sourceType` and tenant the upserts use (read how the loop computes them for upserts and reuse
  that), and `DetectedAt` from the pipeline's clock, or `DateTimeOffset.UtcNow` if the pipeline has no `TimeProvider`. Call
  `DeleteAsync` on every exporter that implements `IDeletionExporter`, in registration order. Count it toward
  `sinceCommit`.
- A deletion exporter that throws stops the run **without committing**, the same rule as an exporter failure on upsert
  (`exporterFailuresStop`). Read how the loop handles that today and mirror it.
- Before the loop starts: if `source.ReportsDeletions` is true and no exporter implements `IDeletionExporter`, throw
  `InvalidOperationException("This connector reports deletions but no exporter can apply them.")`. Fail closed.
- Report: add `int ItemsDeleted = 0` as the last optional parameter of `IngestionReport`, and set it from the run.
- `FlushAsync` is already called before each commit. Make sure deletion exporters are flushed too (they are also
  `IConnectivityExporter`s, or document that a deletion-only exporter must implement both).

Tests (`OpenTail.Connectivity.Tests/IncrementalSyncTests.cs`, extend its `Scripted` connector with a `"D:<ref>"` step that yields
`Delete(ref, DeletionReason.Deleted)`, and a `"D-:<ref>"` step for `OutOfScope`, plus a way to set `ReportsDeletions`):
1. `U:a, D:b, C:1` with a recording deletion exporter: the exporter gets one `DocumentDeletion` with `Id ==
   ConnectivityPipeline.DocumentId(...)` for `b`, after `a` is exported and before the cursor `1` is saved (use the existing
   `LoggingStore` log to check the order).
2. A deletion exporter that throws on `b`: the cursor is not saved, and the report has the error.
3. `ReportsDeletions = true` with no deletion exporter: `RunAsync` throws before reading any change.
4. `MaxItemsProcessed = 1` with `U:a, D:b, C:1`: `b` is not deleted.
5. `ItemsDeleted` counts deletions.

Done when the tests pass and the existing incremental and SQLite tests still pass. Commit `WP1: deletions through the sync core`.

## WP2. JSONL deletion exporter (small) ✅

File: `Export/JsonlDeletionExporter.cs`, next to `JsonlDocumentExporter.cs` (copy its file handling, flush and disposal pattern).
- `public sealed class JsonlDeletionExporter : IConnectivityExporter, IDeletionExporter`. `ExportAsync` does nothing (it is
  deletion-only). `DeleteAsync` appends one JSON line per deletion: `{"id","sourceRef","sourceType","tenantContext","reason","detectedAt"}`,
  camelCase, reason as a lowercase string, `detectedAt` in ISO 8601 UTC.
- Tests: two deletions give two parseable lines in order. `FlushAsync` called twice is safe. A pipeline run with
  `JsonlDocumentExporter` + `JsonlDeletionExporter` and a scripted connector writes the upserts to one file and the deletion to
  the other.
- Commit `WP2: JSONL deletion exporter`.

## WP3. Full-run comparison for connectors without a change feed (medium) ✅

Goal: SQL-like connectors can't see deletions incrementally. After a complete full run, the report lists every source ref seen,
so a consumer can remove what it holds and the list lacks. The pipeline itself never deletes by absence.

- `SyncOptions` gains `bool CollectSourceRefs = false` and `int MaxReportedRefs = 100_000` (last, optional).
- `IngestionReport` gains `IReadOnlyList<string>? SeenSourceRefs = null` and `bool SourceRefsTruncated = false` (last, optional).
- Fill `SeenSourceRefs` only when **all** of these hold: `CollectSourceRefs` is on; the run started with no stored cursor or hit a
  `Reset`; the stream ended normally (not stopped by `MaxItemsProcessed`, not by an exporter failure). Add **every** upserted
  ref whatever its outcome (exported, skipped, rejected, failed fetch), because a failed item still exists at the source.
  Otherwise leave it null.
- Over `MaxReportedRefs`: keep the first N, set `SourceRefsTruncated = true`, and add a warning issue
  `SOURCE_REFS_TRUNCATED` saying the list must not be used for deletions.
- Tests (scripted connector):
  1. A full run of `U:a, U:b, C:1` has `["a","b"]`.
  2. A run resumed from a stored cursor has null.
  3. A run stopped by the cap has null.
  4. A `Reset` mid-run makes it a full run, and the refs are collected from the reset on.
  5. A rejected item is in the list.
  6. A cap of 1 truncates with the flag and the warning.
- Also one test through the real SQLite connector: two rows, a full run with `CollectSourceRefs`, both ids listed.
- Commit `WP3: full-run source refs for deletion by comparison`.

## WP4. Retries instead of holding the cursor (medium) **(owner model)** ✅

Problem: in an incremental run, a failed fetch sets `holdCommits`, so nothing after it commits. An item that always fails (a 403,
a file removed between listing and fetch, a corrupt object) pins the cursor at that point forever, and every later run re-reads
everything after it. That's the same class of bug as the SQLite cursor livelock fixed in review S1.

Shape, from `sync-and-deletions.md`: the stored sync state carries a small retry list (ref, attempts, last error, first failed
at). A failed fetch adds the item to the list and lets the cursor move on. Each run first retries listed items through
`FindAsync`, a new optional connector method that fetches one item by ref and returns null if it's gone. An item that succeeds,
is upserted again by the feed, or is deleted leaves the list. After `MaxItemAttempts` (default 5) the item is dropped with an
error in the report, so a permanent failure never blocks the rest.

**As built:** the list rides inside the stored cursor (`otail-retry-v1:` + JSON, only while it is non-empty, so plain cursors and
stores are unchanged and list and cursor are saved in one write). The failed `SourceItem` is stored and retried with the
connector's own `FetchAsync`, so no connector needed a new method; an item that has vanished uses up its attempts and is dropped
with an error. Extraction failures are not retried, as the design says. `SyncOptions.MaxItemAttempts` (5) and `MaxRetryItems`
(100); `IngestionReport.RetriesPending`. The original design notes follow. Decisions needed while building: where the retry list lives
(`SyncState` gains a field, versus a separate key in the store); how `FindAsync` is offered without breaking connectors (a default
interface member returning "not supported", which keeps today's hold-the-cursor behaviour for that connector). Tests follow the
design's list. Kept with the main model.

## WP5. Folder connector reports deletions after a complete walk (small) ✅

File: `Connectors/FolderSourceConnector.cs`. It isn't an incremental connector and keeps its own `FolderSyncState`.
- After `DiscoverAsync` finishes enumerating **completely** (not cancelled, no exception), expose
  `IReadOnlyList<string> DeletedSinceLastScan`: paths in the loaded state that the walk didn't see. Paths excluded by the
  filter count as gone too, but only when the filter is unchanged. Keep it simple: report them, and document that changing the
  filter makes previously matched files appear deleted.
- Before a complete walk the property is empty. `SaveStateAsync` already drops unseen files from the state, so check that a deleted
  file isn't reported twice across runs.
- Tests: three files, scan and save, delete one, scan again: `DeletedSinceLastScan == [that path]`. A cancelled scan reports
  nothing. A third scan after saving reports nothing.
- Commit `WP5: folder connector lists files deleted since the last scan`.

## WP6. Skip unchanged items before fetching (medium) ✅

Goal: when a source gives a version token (ETag, version id, entry `updated`), the pipeline skips fetch, extraction and export for
an item whose token matches the one stored after its last successful export.

- `SourceKeys` gains `public const string Version = "source.version";`, documented as "an opaque token that changes whenever the
  item's content changes: an ETag, a version id. Not a timestamp unless the source guarantees one." Connectors put it in
  `SourceItem.Metadata`.
- New `Contracts/IItemVersionStore.cs`:
  ```csharp
  public interface IItemVersionStore
  {
      Task<string?> GetAsync(string documentId, CancellationToken ct = default);
      Task SetAsync(string documentId, string version, CancellationToken ct = default);
      Task RemoveAsync(string documentId, CancellationToken ct = default);
  }
  ```
  And `Connectors/FileItemVersionStore.cs`: one JSON file, `AtomicFile` writes, and the same in-process `SemaphoreSlim` plus
  cross-process `.lock` file as `FileSyncCursorStore` (copy that pattern). Writes are batched: `SetAsync` updates memory, and a
  `FlushAsync` writes the file. Add `FlushAsync` to the class only, not the interface, and have the pipeline call it after the
  exporters flush, if the store is a `FileItemVersionStore`. Simpler alternative if that's awkward: write on every `SetAsync`,
  and note the cost.
- `PipelineOptions` gains `IItemVersionStore? VersionStore = null` (last, optional).
- Pipeline, for both normal and incremental runs: before fetching an item, if `VersionStore` is set and the item has
  `source.version`, compute the document id and compare. If they're equal, count it in `ItemsSkipped` with a new report counter
  `ItemsUnchanged = 0` (last, optional), and don't fetch. After the item's document is exported and the exporters have flushed,
  call `SetAsync`. On a WP1 deletion, call `RemoveAsync`. An item without `source.version` is processed as today.
- The existing content-hash dedup stays as it is: a separate guarantee, applied after extraction.
- Connectors that set `source.version` in this WP:
  - `RssFeedConnector`: the entry's `guid`/`id` plus its `updated`/`pubDate` text, joined with `|`, when present.
  - `SiteCrawlConnector`: the response's `ETag` header when present (it fetches during discovery, so this saves extraction and
    export, not the download; say so in a comment). Don't add conditional requests in this WP.
- Tests:
  1. Pipeline: an item with version `v1` exported once; a second run with `v1` doesn't call `FetchAsync`, and counts unchanged; a
     third run with `v2` fetches.
  2. An item without a version is always fetched.
  3. A failed export doesn't store the version.
  4. `FileItemVersionStore` round-trips across instances.
  5. RSS sets the version from guid and updated.
  6. The site crawl sets it from `ETag`.
- Commit `WP6: skip unchanged items by source version`.

## WP7a. Connector conformance suite, plus the first three connectors (medium) ✅

Model: `OpenTail.Connectivity.Memory/Conformance/MemoryBackendConformance.cs`, a public class that runs named checks against
any backend and fails with a clear message. Do the same for connectors.

- New `OpenTail.Connectivity.Files/Conformance/SourceConnectorConformance.cs`, public, with:
  ```csharp
  public interface IConnectorFixture
  {
      ISourceConnector Create();                    // over the fixture's data
      ISourceConnector CreateEmpty();               // over an empty source
      Task RemoveOneItemAsync(string sourceRef);    // make one item vanish, for the fetch check
      IReadOnlyList<string> SecretsGiven { get; }   // strings that must not appear in exception messages
  }
  public static class SourceConnectorConformance
  {
      public static Task RunAsync(IConnectorFixture fixture, CancellationToken ct = default);
  }
  ```
- Checks, from `testing-and-verification.md` ("Conformance tests every connector must pass"), numbered the same:
  1. Discovered items have a non-empty `Id`, a tenant context and a `Modified` that is not `default`.
  2. Ids are unique within a run and identical across two runs over unchanged data.
  3. `FetchAsync` of a discovered item succeeds. After `RemoveOneItemAsync`, fetching that item returns empty bytes or throws an
     ordinary exception within 10 seconds, never hangs. Use a timeout.
  4. Cancelling during enumeration stops it within 5 seconds. Cancel after the first item; a fixture with one item may skip
     this, with a note.
  5. No public method on the connector's type starts with a write verb. Reuse `ReadOnlyInboxGuard`'s verb list; make it
     reusable if needed without changing its behaviour.
  6. Exception messages from 3 and from a deliberately broken fixture (optional; skip if the fixture can't break) contain none
     of `SecretsGiven`.
  7. `CreateEmpty()` yields nothing and doesn't throw.
  8. For `IIncrementalSourceConnector`: a run from no cursor, then from its last checkpoint, yields no upserts when nothing
     changed. Resuming from the first checkpoint repeats exactly the items after it.
- Apply it in `OpenTail.Connectivity.Tests/Conformance/` to: `FolderSourceConnector` (temp folder), `CsvSourceConnector`
  (temp file), `SqliteSourceConnector` (temp database, including check 8). Each is one small fixture class plus one `[Fact]`
  calling `RunAsync`.
- If a connector fails a check, **don't change the suite to make it pass.** Fix the connector if the fix is small and obvious
  (one or two lines), and otherwise mark that check skipped for that fixture with a `// CONFORMANCE GAP:` comment and list it in
  your report.
- Commit `WP7a: connector conformance suite`.

## WP7b. Conformance for the remaining connectors (medium) ✅

After WP7a, apply the suite to `EmlFileConnector`, `MboxConnector` (temp files), `RssFeedConnector` and `SiteCrawlConnector` (stub
`HttpMessageHandler`, as their existing tests do), and the four social connectors (stub handlers; reuse what `SocialTests` does).
Same gap rule. Commit `WP7b: conformance for every connector`.

## WP8. Verification labels (docs, small) ✅

Files: `README.md` (the package table) and the `<Description>` of each packable project (in its `.csproj`, or via
`Directory.Build.props` if that's where descriptions live).
- Add a "Verification" column to the README package table, and a short legend copied from `testing-and-verification.md`
  ("Labelling"). Use these labels exactly (the owner model's assessment; don't change them):
  - **Verified** (exercised against the real thing in CI): `Files` (folder, local filesystem), `Csv`, `Email` (EML and mbox
    files), `Sql` (real SQLite), `Memory`, `Memory.Sqlite`, `Memory.VectorData` (pgvector in `memory-pgvector.yml`),
    `Calendar.Ics` (files).
  - **Tested** (unit and stubbed-service tests only): `Web`, `Social`, `Inbox.Imap`, `Inbox.Graph`, `Inbox.Gmail`,
    `RichText`, `Storage`, `Storage.S3`.
  - If the table has a package not listed here, label it **Tested** and say so in your report.
- Append " Verification: <label>." to each package description.
- Commit `WP8: verification labels`.

## WP9. Web regression gaps (small) ✅

Files: tests for `RssFeedConnector` and `SitemapParser` (extend the existing test files).
Add cases only where they're missing (check first):
- RSS: an RSS 2.0 feed and an Atom feed with the same entries map to the same titles and links. Duplicate entries (same guid) are
  yielded once. Malformed XML gives an empty result or a clean error, not a crash. An entry without a link is skipped or handled
  as the code intends; pin the current behaviour.
- Sitemap: a sitemap index that refers to itself (a cycle) terminates. An index nested beyond the depth limit stops. Duplicate
  URLs across child sitemaps appear once. URLs on another origin are dropped. Gzip sitemaps (`.xml.gz`): if they're not
  supported, add a test pinning that they're ignored, and note it.
- Fix only one- or two-line bugs these tests expose. Report anything larger.
- Commit `WP9: RSS and sitemap regression tests`.

---

## Not planned here, with references for when they are

- **Event-driven ingestion and job queues:** the Host's triggers design (plan section 21). When built, read Rag.NET's
  `src/Rag.NET.DataProviders/EventDriven/ChannelIngestionJobQueue.cs` (bounded channel, backpressure, per-job failure isolation)
  and `src/Rag.NET.Api/Webhooks/WebhookSignatureValidator.cs` (HMAC-SHA256, constant-time compare). An in-memory channel isn't
  durable. The triggers design already requires surviving a power cut.
- **SaaS connectors** (Google Drive, Graph files, Notion, Slack, Zendesk), when there's demand. Useful references:
  `src/Rag.NET.DataProviders.GoogleDrive/GoogleDriveDataProvider.cs` (`Changes.List` with page tokens, `md5Checksum` as a version
  token), `src/Rag.NET.DataProviders.Microsoft365/*` (Graph delta), and the change-token handling in the Gmail and Linear
  providers. Each would be an `IIncrementalSourceConnector` that reports deletions (WP1) and sets `source.version` (WP6).
  `docs/design/google-drive.md` and `microsoft-graph-files.md` already describe them.
- **Unchanged-item skipping, upstream:** Rag.NET's version is in `src/Rag.NET/DataProviders/RagPipelineExtensions.cs` (~line 397:
  compare the stored ETag before fetching, refresh it only after success).
