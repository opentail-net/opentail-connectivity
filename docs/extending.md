# Building on OpenTail Connectivity

Yes: another project can add to the scope without changing this repository. The extension points are public interfaces and the
design is built for composition. It is not built for inheritance. This page says what works today and where it is awkward.

## What works today

| You want to | How | Notes |
|---|---|---|
| Add a new source | Implement `ISourceConnector` and an `ITextExtractor` in your own assembly. | Reference `OpenTail.Connectivity.Files` only. See [writing-a-connector.md](writing-a-connector.md). |
| Add a new output | Implement `IConnectivityExporter`. | Several can run in one pipeline. |
| Change documents on their way through | `PipelineOptions.Transformers`: each runs on the normalised document, in order, before validation and export; the content hash is taken after them. | Use it for redaction and enrichment. Wrapping the `ITextExtractor` or an `IConnectivityExporter` still works for changes those hooks cannot make. |
| Pass facts from a connector | `SourceItem.Metadata` | Carried onto the document (permissions, URLs, `source.version`); see [design/shared-groundwork.md](design/shared-groundwork.md). |
| Know a document's id in advance | `ConnectivityPipeline.DocumentId(sourceType, tenant, sourceRef)` | Stable across runs; call it rather than reimplementing the hash. |
| Add a new inbox or calendar provider | Implement `IInboxAdapter`, `ICalendarAdapter` (or only `ICalendarReader`), `IEmailSender` and `IMailboxManager`. | Run `ReadOnlyInboxGuard.AssertReadOnly` on inbox adapters in your tests. Check writes against a `CalendarWritePolicy`, as the Graph and Gmail adapters do. |
| Label your documents | Pass your own `sourceType` string to `ConnectivityPipeline`. | It is free text, lower-cased. |
| Stamp run-level facts | `PipelineOptions.ExtraMetadata` | Appears under `run.*`. |
| Use only what you need | Reference single packages. | Only `Files` is shared; `Web`, `Csv`, `Email`, `Sql`, `Social`, `Inbox.*` and `Calendar.Ics` depend on it, not on each other. |

All packages share one version, so reference the same version of every OpenTail package in a project.

## Where it is awkward

These follow from how the code is written today.

- **Nothing can be subclassed.** Every public class is `sealed` and there are no virtual members. `ConnectivityPipeline`, the folder
  connector and the senders can be wrapped, not overridden. Wrapping works because the pipeline takes interfaces.
- **The normalisation step cannot be replaced.** It is a private static method inside the pipeline, so how the title, hash, ID
  and base metadata are first built is fixed. Transformers can change the result afterwards. Extractor metadata is always stored
  with an `extraction.` prefix.
- **No dependency-injection helpers.** There is no `AddOpenTail...` extension; you construct objects yourself.
- **Packages are on GitHub Packages**, which needs a token even to read. Consumers outside the organisation need their own
  access setup (see the README).
- **Dependencies come along.** `Files` pulls in `Transformations.Text`, and each inbox package pulls in its provider SDK.
- **Everything is 0.x.** Public types can change between minor versions until a 1.0 is declared.

## A minimal extension

```csharp
// In your own project, referencing OpenTail.Connectivity.Files.
public sealed class TicketConnector : ISourceConnector
{
    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // call your system, yield one SourceItem per ticket (stable Id, real Modified time, tenant context)
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken ct = default)
    {
        // return the ticket as bytes your extractor understands, for example JSON
    }
}

public sealed class TicketTextExtractor : ITextExtractor
{
    public TextExtractionResult Extract(string fileName, byte[] content) { /* bytes -> text + metadata */ }
}

var pipeline = new ConnectivityPipeline(new TicketConnector(), new TicketTextExtractor(), [exporter], sourceType: "ticket");
```

This sketch has not been compiled.

## Making the project easier to extend

These are proposals for the maintainers. Source-item metadata, stable document IDs and the transformer hook, proposed here
earlier, are implemented (see the table above).

1. *Done 2026-10-22:* `IEmailSender` is send only and `IMailboxManager` holds delete, move and mark-read, as `ICalendarReader`
   is split from calendar writes. The Host's Email module adopts it when it takes the next packages.
2. Add an optional `Microsoft.Extensions.DependencyInjection` extension package, kept separate so `Files` stays light.
3. Declare which types are public API and stable, and treat the rest as internal, ahead of a 1.0.
