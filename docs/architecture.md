# Architecture

OpenTail Connectivity is a set of small libraries that turn content from many places into one shape, `NormalisedDocument`.
Everything builds on `OpenTail.Connectivity.Files`; the other packages add sources.

## The pipeline

```
ISourceConnector --> ITextExtractor --> normalise --> DocumentValidator --> IConnectivityExporter(s)
  DiscoverAsync       Extract            (hash, ids,     (errors fail the        (JSONL, Markdown)
  FetchAsync                              metadata)       item, warnings kept)
```

`ConnectivityPipeline.RunAsync` drives it and returns an `IngestionReport`:

1. `DiscoverAsync` streams `SourceItem`s (id, display name, extension, size, modified time, tenant context).
2. `FetchAsync` returns the item's bytes.
3. `ITextExtractor.Extract` turns bytes into text and optional metadata. A failed extraction counts as *failed*; empty text counts
   as *skipped* with an `EMPTY_CONTENT` warning.
4. The result is normalised into a `NormalisedDocument` and checked by `DocumentValidator`. Any error-severity issue fails the
   item. Normalisation sets:
   - `Body`: the extracted text, trimmed. `ContentHash` is the SHA-256 of that body, so title and metadata changes do not change it.
   - `Title`: `item.DisplayName`, without the item's own extension when it ends with it (`report.pdf` becomes `report`; a
     message titled "Release 1.2" stays whole).
   A source that implements `ISourceItemFeedback` is told which items failed or were left by the item cap, so it can keep
   them out of its sync state.
   - `SourceRef`: the item's `Id`.
   - `Id`: stable: `ConnectivityPipeline.DocumentId(sourceType, tenant, SourceRef)`, the first 32 hex characters of a SHA-256
     over the three (each length-prefixed). The same item gets the same id every run, so a store can update in place; compare
     `ContentHash` to see whether it changed. Two items with the same `SourceRef` in one run give a `DUPLICATE_SOURCE_REF`
     warning. Documents from versions before this have random ids; re-ingest once.
   - `Metadata`: `source.extension` and `source.sizeBytes`, then every extractor key prefixed with `extraction.`, then every
     `PipelineOptions.ExtraMetadata` key prefixed with `run.`. So a key the email extractor calls `email.subject` is stored as
     `extraction.email.subject`.
5. Each `IConnectivityExporter` receives the document. `FlushAsync` is called at the end of the run. An exception from an exporter
   counts the item as failed, but exporters that already received it keep it.

The report is written separately: pass the returned `IngestionReport` to `JsonReportExporter.ExportAsync`. It is not an
`IConnectivityExporter`.

Items dropped as duplicates (when deduplication is on) count as skipped. `MaxItemsProcessed` caps documents produced, not items
discovered.

`PipelineOptions` controls content-hash deduplication (`DeduplicateByContentHash`, bounded by `MaxDedupHashes`; per run and per
tenant, and a hash counts only once every exporter has taken the document), extra metadata
stamped on every document, and a cap on items processed.

### Validation codes

| Code | Severity | Meaning |
|---|---|---|
| `MISSING_BODY` | Error | Body is empty. |
| `MISSING_SOURCE_REF` | Error | No source reference. |
| `MISSING_TITLE` | Warning | No title. |
| `MISSING_TENANT` | Warning | Tenant context not set. |
| `SHORT_CONTENT` | Warning | Body is shorter than 50 characters. |
| `MISSING_HASH` | Warning | No content hash. |

## Redaction and other transformers

`PipelineOptions.Transformers` run in order on each document after normalisation and before validation and export, and the
pipeline takes `ContentHash` after them, so a hash never reflects text that was removed. A transformer that throws fails the item,
which is not exported (fail closed). `PatternRedactor` is the built-in one: opt-in rules for email addresses, phone numbers
(nine digits or more, not dates), payment card numbers (Luhn-checked), IBANs (checksum-checked) and custom patterns, replaced by
`[REDACTED:<rule>]` in the title, body and metadata values, with `redaction.count` in the metadata. It is a safety net, not a
compliance control: it misses names, addresses and free-text identifiers.

## Incremental runs and deletions

A source that implements `IIncrementalSourceConnector` streams changes since a cursor: `Upsert` items, `Delete` events,
`Checkpoint` cursors and `Reset` when its stored cursor was rejected. Give the pipeline an `ISyncCursorStore`
(`FileSyncCursorStore(path)`: one JSON file, many scopes) in `PipelineOptions.SyncStore` and it runs incrementally:

- It loads the cursor for the connector's `SyncScope` and passes it to `GetChangesAsync` (null: a full run).
- At a checkpoint, once `SyncOptions.CheckpointEveryItems` (500) changes or `CheckpointEvery` (5 minutes) have passed, it
  **flushes every exporter and only then saves the cursor**. At the end of the stream, or at `MaxItemsProcessed`, it commits the
  last checkpoint reached.
- Each save carries the version it loaded; if another run saved meanwhile, `SyncConflictException` and nothing is saved.
- A failed fetch (network, throttling) adds the item to a **retry list** stored with the cursor (up to `MaxRetryEntries`, default
  10,000). Each run first retries listed items before reading new changes. After `MaxItemAttempts` (default 3), a permanently
  failing item is dropped with an error and the cursor moves on. A rejected item (extraction or validation failed) is not retried:
  the same bytes would fail again.
- An exporter or stream exception stops the run; nothing after the last commit is saved. A crash between flush and save repeats
  some items next run, which stable ids make harmless.
- The report's `SyncScope`, `FullRun`, `SyncCommitted`, `ItemsRetried` and `ItemsAbandoned` say what happened.

### Deletions

Connectors with a change feed (`ReportsDeletions = true`) yield `SourceChange.Delete(sourceRef, reason)` when an item is removed
or leaves scope. Each deletion is passed to exporters implementing `IDeletionExporter` as a `DocumentDeletion` with the same
stable `Id` the upsert had. Deletions are processed in order with upserts and committed at checkpoints.

Connectors without a change feed (SQL, object storage) cannot see deletions incrementally. With `SyncOptions.CollectSourceRefs`,
a **full run** that completes fills `IngestionReport.SeenSourceRefs` with every source ref discovered, whatever its outcome.
Consumers compare this list with what they hold to find deletions by absence. `SourceRefsTruncated` warns when the list hit
`MaxReportedRefs` (default 100,000).

The pipeline never infers a deletion from absence itself; it only reports what the connector says or lists what it saw.

### Version-based skipping

When a source provides a version token (ETag, content hash, last-modified), the pipeline can skip fetching unchanged items. Set
`PipelineOptions.VersionStore` to an `IItemVersionStore` (`FileItemVersionStore(path)`). The pipeline stores each exported
document's `source.version` metadata and skips re-fetching when the token matches. This works across both incremental and full
runs. Connectors set `source.version` in `SourceItem.Metadata`; RSS and Atom feeds use a hash of every payload field (see [connectors](connectors.md#web)), web crawlers use HTTP `ETag`.

Without a version store, every item is fetched and extracted as before.

## Packages

| Package | Source | Notes |
|---|---|---|
| `Files` | Folders and files | Include/exclude patterns, size and depth limits, noisy-directory skipping, incremental sync state with deletion detection. Also holds the contracts, models, exporters, and the shared inbox and calendar abstractions. |
| `Web` | URLs | Same-domain crawling with sitemap support, version-based skipping via HTTP ETag. |
| `Csv` | CSV rows | `CsvConnectorConfig` names the id, body, title, modified and metadata columns; only listed columns reach the document. |
| `Email` | `.eml`, `.mbox` | Built on MimeKit. |
| `Sql` | SQLite databases | Allow-list of fields. Incremental connector with full-run source ref collection. Despite the package name, only SQLite is implemented. |
| `Social` | X, Reddit, YouTube, Meta | Sentiment scoring and marketing metadata. |
| `Inbox.Imap`, `Inbox.Graph`, `Inbox.Gmail` | Mailboxes | Implement `IInboxAdapter` plus attachment and thread fetchers; Graph and Gmail also provide calendar adapters. |
| `Drive.Google`, `Drive.Graph` | Google Drive, OneDrive/SharePoint | Incremental connectors with change feeds: deletions, checkpoints, version skipping. |
| `Calendar.Ics` | ICS feed URLs | Implements `ICalendarAdapter` read-only. It downloads an ICS file over HTTP (cached for 30 seconds by default); it does not speak the CalDAV protocol. |
| `Storage`, `Storage.S3`, `Storage.AzureBlob` | Object storage | Read-only adapters over S3 and Azure Blob Storage. Generic `ObjectStorageConnector` provides incremental sync with watermarks. |

## Read by default, with opt-in writes

Source connectors (folders, web, CSV, `.eml`/`.mbox`, SQL, social) only read. The inbox and calendar packages are different:
they include code that can change a mailbox or calendar. What can write, and what stops it, is set out in
[safety-and-credentials.md](safety-and-credentials.md). In short:

- `IInboxAdapter` (fetching mail) is read-only. `ReadOnlyInboxGuard.AssertReadOnly(type)` checks by reflection that an
  implementation has no public write-style methods (`Send*`, `Delete*`, `Move*` and similar) and throws if it does. Nothing in the
  library calls it; call it yourself, for example in a test.
- `IEmailSender` (send, delete, mark read, move) is separate and is gated by `EmailSenderPolicy`: sending is off by default and
  needs human approval by default.
- `ICalendarAdapter` includes create, update, delete and respond. The Graph and Gmail adapters implement them for real, with
  **no policy gate**; the ICS adapter throws `NotSupportedException`.

## Tenant context

Every `SourceItem` and `NormalisedDocument` carries a `TenantContext` string, so documents from different owners can share a
store and still be told apart. Most connectors take it as a constructor argument. The social connectors do not: they leave it
empty, so their documents get a `MISSING_TENANT` warning and you have to set the tenant yourself, for example in a wrapping
exporter. The pipeline reports the first item's tenant context in the `IngestionReport`.
