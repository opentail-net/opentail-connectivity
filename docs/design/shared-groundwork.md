# Design: shared groundwork

**Status:** proposal. Nothing here is implemented, and the code blocks are sketches that have not been compiled. They show the
shape of the change so the maintainers can react before any code is written.

This covers the shared pieces named in [roadmap.md](../roadmap.md) that every new connector would otherwise reinvent, all inside
`OpenTail.Connectivity.Files`. No new package and no new dependency is needed.

## Principles

- Backwards compatible where possible. Where not, say so (the project is 0.x and every package ships at one version).
- No new dependencies. Use `System.Text.Json`, `System.Net.Http`, `System.Security.Cryptography`.
- Safe by default. Missing information means "restricted" or "unknown", never "public".
- Each piece stands alone and can ship as its own pull request, in the order below.

## 1. Stable document IDs

**Problem.** `NormalisedDocument.Id` is a new random GUID per run (`ConnectivityPipeline.Normalise`). A downstream store cannot
update a document in place or notice that one was removed.

**Proposal.** Derive the ID from what identifies the document:

```
Id = lowercase hex of SHA-256( sourceType + "\n" + tenantContext + "\n" + sourceRef ), first 32 characters
```

`SourceRef` is the connector's `SourceItem.Id`, which most connectors already make stable (path, URL, row id, message id). The
exceptions to fix or document first: `MboxConnector` uses a random id for a message without `Message-Id`, and the folder and
`.eml` connectors use the path as given, so a different spelling of the root changes every id. `InboxEmailDocumentMapper`
already derives a stable `Id` (a SHA-256 of message id and tenant), which is precedent for this change.

**Behaviour.**
- The same item in the same tenant and source type gets the same ID on every run. A changed body gives a new `ContentHash` but the
  same `Id`: the store updates in place.
- Two items with the same `SourceRef` in one run get the same `Id`. That is a connector bug; the pipeline can add a validation
  warning `DUPLICATE_SOURCE_REF`.
- Documents written by earlier versions keep their random IDs. Consumers who stored those need a one-off re-ingest.

**Deletions.** Designed in [sync-and-deletions.md](sync-and-deletions.md): connectors with a change feed report deletions as
data, and a full run can list every ref it saw (`SeenSourceRefs`) for consumers to compare with their store.

**Checks to make first.** Search the tests for anything that assumes IDs differ between runs or documents.

## 2. Item metadata and source permissions

**Problem.** `SourceItem` has fixed fields and `ITextExtractor.Extract` receives only a name and bytes, so a connector cannot pass
facts such as an owner, a URL or who may read the item. See [extending.md](../extending.md).

**Proposal.** Add an optional property bag to `SourceItem`:

```csharp
public sealed record SourceItem(
    string Id, string DisplayName, string Extension, long SizeBytes,
    DateTimeOffset Modified, string TenantContext,
    IReadOnlyDictionary<string, string>? Metadata = null);
```

The pipeline copies it into `NormalisedDocument.Metadata` under the `source.` prefix, next to the existing `source.extension` and
`source.sizeBytes`. The parameter is last and optional, so existing source code compiles, but the constructor signature changes,
so already-built consumers need rebuilding. All packages move together, which keeps this manageable.

**Standard keys.** Connectors fill in what they can. Every key is optional.

| Key | Meaning |
|---|---|
| `source.url` | Link a person can open. |
| `source.container` | Folder, site, space, project or channel the item belongs to. |
| `source.owner` | Owner or author identifier. |
| `source.acl.visibility` | `private`, `restricted`, `organisation`, `public`, or `unknown`. |
| `source.acl.principals` | JSON array of user, group or role identifiers allowed to read the item. |
| `source.acl.captured` | `true` if the connector actually read permissions from the source. |

**Rules that matter.**
- **Absence is not permission.** If `source.acl.captured` is missing or `false`, consumers must treat the item as restricted. State
  this in the docs and in `IngestionReport` warnings.
- Permissions describe the source at fetch time. Re-ingest to refresh them. A revoked permission is only visible on the next run.
- Identifiers are the source system's own (an object id or email address) and are not mapped to the consumer's users. Mapping is
  the consumer's job.
- Cap the principal list (for example 500 entries) and set `source.acl.visibility` to `restricted` with a truncation flag if
  exceeded, instead of silently dropping people.
- Reading permissions often needs a broader scope than reading content. Record that for each connector in its design page, and
  make it optional so a content-only run still works.

## 3. Document transformer hook (redaction and enrichment)

**Problem.** Today a document can be changed in two places, neither built for it. A wrapping `ITextExtractor` can rewrite the body
before hashing, but cannot see the title, which the pipeline derives from `DisplayName`. A wrapping exporter sees everything but
runs *after* the hash and validation, so the hash reflects the unredacted text.

**Interim, no core change.** Provide a `RedactingTextExtractor : ITextExtractor` that wraps another extractor and redacts the body
and extractor metadata, so the hash is of the redacted text, and document that titles are not covered.

**Proposal.**

```csharp
public interface IDocumentTransformer
{
    NormalisedDocument Transform(NormalisedDocument document);
}
// PipelineOptions gains: IReadOnlyList<IDocumentTransformer>? Transformers = null
```

The pipeline runs transformers after normalisation and before validation, then recomputes `ContentHash` from the final body. So:
- the hash never reflects text that was removed (a hash of an email address or card number can be guessed back);
- validation sees what will be exported;
- an exception in a transformer fails that item and is reported, and the item is not exported. Redaction must fail closed.

**Built-in `PatternRedactor`** (opt-in rules, none on by default): email addresses, phone numbers, payment card numbers (with a
Luhn check), IBANs, and custom regex rules. It rewrites the title and metadata values as well as the body, replaces matches with
`[REDACTED:<rule>]`, and records `redaction.count` in the metadata.

**Be honest about limits.** Pattern matching is best-effort. It misses names, addresses and free-text identifiers, and it can match
things that are not personal data. It is a safety net, not a compliance control, and the docs must say so.

## 4. One incremental-sync abstraction

**Problem.** Folder sync state, `GmailSyncStateStore`, IMAP and Graph state each have their own type and file format. Each new
connector would add another. The three inbox stores write their file directly, so a crash mid-write can corrupt it, whereas the
folder store writes a temporary file and moves it into place. The inbox adapters also save their cursor themselves when
enumeration ends, before the caller has processed the messages, and the folder state records files that failed extraction. So
today a failed item is not retried: delivery is at most once, not at least once.

**Proposal.** Designed in full in [sync-and-deletions.md](sync-and-deletions.md). In short: an `ISyncCursorStore` holding one opaque cursor, a retry list and a version per scope; connectors that opt in through
`IIncrementalSourceConnector` stream changes with checkpoints; and the pipeline, not the connector, saves the cursor after
flushing the exporters. The points below still hold.

- **Scope** is a string the connector builds from its type, tenant and source identity, for example
  `graph-drive:acme:<driveId>`. One file can hold many scopes.
- **Cursor** is whatever the source understands: a delta token, a history id, an ISO timestamp, a page token. The store never
  parses it.
- **Commit after success.** The pipeline saves a checkpoint only after every item before it has been handled (exported, skipped,
  rejected or queued for retry) and the exporters have flushed. A crash between flush and save means the next run repeats some items (at-least-once).
  With stable IDs (section 1), repeats are harmless.
- **Reset.** Deleting the scope forces a full run. A corrupt file is treated as no state, as the folder store does today.
- Existing stores keep working. Migrating them is optional and separate.

## 5. REST helper

**Problem.** Web, Twitter, Reddit, YouTube and Meta each implement their own request, retry and delay logic.

**Proposal.** A sealed `RestClient` over an `HttpClient` the caller provides:

- `GetJsonAsync<T>` with `System.Text.Json`.
- Retries on 429 and 5xx with exponential backoff and jitter, a ceiling, and `Retry-After` honoured. No retries for other 4xx.
  Bounded attempts and a cancellation token on everything.
- A minimum delay between requests (as `RequestDelayMs` does today).
- An auth callback `Func<CancellationToken, Task<string>>` that returns a fresh token, so rotation works.
- Paging helpers for the four common shapes: next-link in the body (Microsoft Graph), cursor token (Slack, Notion), page number
  (many REST APIs), and `Link` header (GitHub).
- **Same-host rule for next links.** A next link is followed only if it is on the same host as the request. Otherwise the call
  fails. This stops a malicious or buggy response from sending the bearer token to another server.
- Error messages are passed through `CredentialRedactor`, and response bodies are capped before they are put in exceptions.
- A response size cap, so one huge response cannot exhaust memory.

Tests use a fake `HttpMessageHandler`. No new dependency is needed.

## 6. Read-only checks for every connector

**Problem.** `ReadOnlyInboxGuard` checks only inbox adapters, and the calendar and sender contracts mix read and write.

**Proposal.**
- A test that scans every `ISourceConnector` implementation in all packages and fails if it exposes a public method whose name
  starts with a write verb (the guard's list). New connectors are covered automatically.
- Each provider package exposes read-only credential builders as the documented default, and writes need a deliberately named
  alternative. `GmailScopes.ReadOnly` exists but `GmailCredentialProvider` always uses `GmailScopes.Full`; Graph uses the
  app's granted permissions through `.default`, so for Graph the control is the permission grant, not a builder.
- Split read and write contracts where they are mixed (`ICalendarAdapter`, `IEmailSender`). This is a breaking change, so it is
  listed as an open question, not part of the first step.

## 7. File filters shared by file-like connectors

**Problem.** The Graph files, Google Drive and object storage connectors need the folder connector's include/exclude patterns,
extension list and size limit. The options sit in `FolderConnectorOptions` and the matching in `WildcardMatcher`, which is
`internal`, so a new package would have to copy them and drift.

**Proposal.** A public record and matcher in `Files`, used by the folder connector too:

```csharp
public sealed record FileFilterOptions(
    IReadOnlyList<string>? IncludePatterns = null,
    IReadOnlyList<string>? ExcludePatterns = null,
    IReadOnlyList<string>? SupportedExtensions = null,   // replaces the default list
    IReadOnlyList<string>? ExtraExtensions = null,       // adds to it
    long? MaxFileSizeBytes = null);

public sealed class FileFilter
{
    public FileFilter(FileFilterOptions? options);
    public bool Includes(string relativePath, long sizeBytes);   // patterns, extension and size
}
```

Pattern semantics stay exactly as `WildcardMatcher` has them: `*` within a path segment, `**` across segments, case-insensitive,
`\` treated as `/`, exclude before include. The default extension list moves from `FolderSourceConnector` into `FileFilter`.
`FolderConnectorOptions` keeps its current properties for compatibility and builds a `FileFilterOptions` from them. The existing
folder pattern, extension and size tests become `FileFilter` tests, so the semantics are pinned before other connectors use them.

## Order and size

1. Stable IDs (small).
2. `SourceItem.Metadata` and the `source.*` keys (small, touches every connector's `SourceItem` calls only if they opt in).
3. Cursor store, change stream and deletions ([sync-and-deletions.md](sync-and-deletions.md); medium, additive).
4. Transformer hook and `PatternRedactor` (medium).
5. REST helper (medium).
6. Read-only scan test (small).
7. `FileFilterOptions` and `FileFilter` (small; needed before the Graph files, Drive and object storage connectors).

## Open questions

- Is changing the `SourceItem` constructor acceptable at 0.x, or should the metadata arrive as a separate type?
- Should `Id` include the tenant? Including it keeps tenants apart; leaving it out lets the same source be shared across tenants.
- Is the read/write contract split worth the break now, or after 1.0 planning? The Host's Email and Calendar modules consume
  `IInboxAdapter`, `IEmailSender` and `ICalendarAdapter` (and its tests implement `IInboxAdapter`), so a split must be made
  together with the Host.

Answered: the Host does not rely on random document IDs. As of `opentail-host` `3dd97bb` it uses the inbox and calendar
adapters, the email senders and `HtmlTextExtractor`, but not the pipeline, `SourceItem`, `NormalisedDocument` or the exporters, so
changes to those (sections 1 to 4) do not reach it.
