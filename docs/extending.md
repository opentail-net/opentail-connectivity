# Building on OpenTail Connectivity

Yes: another project can add to the scope without changing this repository. The extension points are public interfaces and the
design is built for composition. It is not built for inheritance. This page says what works today and where it is awkward.

## What works today

| You want to | How | Notes |
|---|---|---|
| Add a new source | Implement `ISourceConnector` and an `ITextExtractor` in your own assembly. | Reference `OpenTail.Connectivity.Files` only. See [writing-a-connector.md](writing-a-connector.md). |
| Add a new output | Implement `IConnectivityExporter`. | Several can run in one pipeline. |
| Change documents on their way through | Wrap the `ITextExtractor` to change the body and extractor metadata before hashing and validation, or wrap an `IConnectivityExporter` to change the finished document. | An extractor wrapper is the better place to redact the body, because the hash is then of the redacted text. Title, `SourceRef` and tenant can only be changed in an exporter wrapper (`NormalisedDocument` is a record, so use `with { ... }`). |
| Add a new inbox or calendar provider | Implement `IInboxAdapter`, `ICalendarAdapter`, `IEmailSender`. | Run `ReadOnlyInboxGuard.AssertReadOnly` on inbox adapters in your tests. |
| Label your documents | Pass your own `sourceType` string to `ConnectivityPipeline`. | It is free text, lower-cased. |
| Stamp run-level facts | `PipelineOptions.ExtraMetadata` | Appears under `run.*`. |
| Use only what you need | Reference single packages. | Only `Files` is shared; `Web`, `Csv`, `Email`, `Sql`, `Social`, `Inbox.*` and `Calendar.Ics` depend on it, not on each other. |

All packages share one version, so reference the same version of every OpenTail package in a project.

## Where it is awkward

These follow from how the code is written today.

- **Nothing can be subclassed.** Every public class is `sealed` and there are no virtual members. `ConnectivityPipeline`, the folder
  connector and the senders can be wrapped, not overridden. Wrapping works because the pipeline takes interfaces.
- **The normalisation step cannot be changed.** It is a private static method inside the pipeline, so you cannot alter how the
  title, hash, ID or metadata are built. You can only change the text and add metadata through the extractor or `ExtraMetadata`,
  or change the result in an exporter. Extractor metadata is always stored with an `extraction.` prefix.
- **A connector cannot pass extra facts to the pipeline.** `SourceItem` has fixed fields, and `ITextExtractor.Extract` receives
  only a file name and bytes. The Sql and Csv connectors work around this by packing the row into JSON bytes for their own
  extractor to read. Anything such as permissions has to travel the same way. The design in
  [design/shared-groundwork.md](design/shared-groundwork.md) proposes a fix.
- **Document IDs change every run**, so a downstream store has to key on `SourceRef` and `ContentHash`.
- **No dependency-injection helpers.** There is no `AddOpenTail...` extension; you construct objects yourself.
- **Contracts mix reading and writing.** `ICalendarAdapter` and `IEmailSender` include write methods, so a consumer that wants only
  reading has to trust the implementation or wrap it. See [safety-and-credentials.md](safety-and-credentials.md).
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

These are proposals for the maintainers. The first three are covered in [design/shared-groundwork.md](design/shared-groundwork.md).

1. Let a `SourceItem` carry metadata, so connectors can pass permissions and URLs through without byte-packing.
2. Make document IDs stable.
3. Add a document-transformer hook in the pipeline for redaction and enrichment.
4. Split read and write contracts (`ICalendarReader` / `ICalendarWriter`, `IEmailSender` into send and manage). The Host's
   Email and Calendar modules use these contracts, so the split has to land in both repositories together.
5. Add an optional `Microsoft.Extensions.DependencyInjection` extension package, kept separate so `Files` stays light.
6. Declare which types are public API and stable, and treat the rest as internal, ahead of a 1.0.
