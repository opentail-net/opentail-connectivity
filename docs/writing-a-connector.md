# Using and writing connectors

## Ingest a folder to JSONL

```csharp
using OpenTail.Connectivity.Files.Connectors;
using OpenTail.Connectivity.Files.Export;
using OpenTail.Connectivity.Files.Extraction;
using OpenTail.Connectivity.Files.Pipeline;

var source = new FolderSourceConnector("/data/docs", tenantContext: "acme");
await using var jsonl = new JsonlDocumentExporter("out.jsonl");

var pipeline = new ConnectivityPipeline(
    source,
    new TransformationsTextExtractor(),
    [jsonl],
    new PipelineOptions(DeduplicateByContentHash: true));

var report = await pipeline.RunAsync();
Console.WriteLine($"{report.DocumentsProduced} documents, {report.ItemsFailed} failed");
```

`FolderSourceConnector` takes an optional `FolderConnectorOptions` (include/exclude patterns, `MaxFileSizeBytes`, `MaxDepth`,
`ModifiedSince`, and so on). For incremental runs, pass a `FolderSyncStateStore` as `stateStore` and call `SaveStateAsync` after
the run. Without a store, `SaveStateAsync` does nothing. The pipeline tells the connector which files it did not process
(`ISourceItemFeedback.NotProcessed`), and those are left out of the saved state, so they are retried next run. A connector with
sync state of its own should implement `ISourceItemFeedback` too.

Check `report.ValidationIssues` and `report.Errors` after a run: warnings do not stop an item, errors do.

## Write a new source

Implement `ISourceConnector` (in `OpenTail.Connectivity.Files.Contracts`):

```csharp
public interface ISourceConnector
{
    IAsyncEnumerable<SourceItem> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default);
}
```

Guidelines, taken from the existing connectors:

- Make `SourceItem.Id` stable and unique within the source; it becomes the document's source reference.
- Put a human-readable name in `DisplayName`. The pipeline uses it as the title, dropping the item's `Extension` only when the
  name ends with it (see [architecture](architecture.md#the-pipeline)).
- Set `Modified` from the source system where there is one, so incremental runs and `ModifiedSince` work.
- Set `TenantContext` on every item.
- Pass what you know about the item in `SourceItem.Metadata`; it lands in the document under `source.` (`SourceKeys`: `url`,
  `container`, `owner`, and the `acl.*` keys). For permissions use `SourceAcl.Captured(visibility, principals)`, or
  `SourceAcl.NotCaptured()` when the connector does not read them. Consumers treat anything not captured as restricted
  (`SourceAcl.IsCaptured`): absence is not permission.
- Read only. Never write back to the source.
- Honour the `CancellationToken`.
- Take an allow-list of fields or columns rather than reading everything (see `CsvConnectorConfig` and the Sql connector).
- Pair the connector with an `ITextExtractor` that understands the bytes you return. For structured sources such as CSV rows,
  the package supplies its own extractor (for example `CsvRowTextExtractor`).

## Write a new exporter

Implement `IConnectivityExporter`: `ExportAsync` is called per document, `FlushAsync` once at the end. The existing exporters are
`JsonlDocumentExporter` and `MarkdownDocumentExporter`. `JsonReportExporter` is separate: it writes the `IngestionReport` that
`RunAsync` returns, not documents.

## Tests

Tests live in `OpenTail.Connectivity.Tests` and run with `dotnet test --solution OpenTail.Connectivity.slnx`. A new connector
should come with tests there.
