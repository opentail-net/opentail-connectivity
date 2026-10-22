# Design: shared groundwork

**Status:** implemented (plan section 26). This was a proposal; all sections below are now built. Stable IDs (section 1), item
metadata (section 2), document transformers (section 3), incremental sync (section 4), and file filters (section 7) shipped.
The REST helper (section 5) remains unbuilt; read-only checks (section 6) are partially covered by the conformance suite.

This document is kept for reference. See [architecture.md](../architecture.md) for current behavior.

This covers the shared pieces named in [roadmap.md](../roadmap.md) that every new connector would otherwise reinvent, all inside
`OpenTail.Connectivity.Files`. No new package and no new dependency is needed.

## Principles

- Backwards compatible where possible. Where not, say so (the project is 0.x and every package ships at one version).
- No new dependencies. Use `System.Text.Json`, `System.Net.Http`, `System.Security.Cryptography`.
- Safe by default. Missing information means "restricted" or "unknown", never "public".
- Each piece stands alone and can ship as its own pull request, in the order below.

## 1. Stable document IDs ✅ IMPLEMENTED

**Problem.** `NormalisedDocument.Id` is a new random GUID per run (`ConnectivityPipeline.Normalise`). A downstream store cannot
update a document in place or notice that one was removed.

**Implementation.** Document IDs are now derived from source identity:

```
Part(s) = s.Length (invariant culture) + ":" + s
Id      = lowercase hex of SHA-256( UTF-8( Part(sourceType) + Part(tenantContext) + Part(sourceRef) ) ), first 32 characters
```

Length-prefixing each part means no characters inside one part can make two different triples hash alike. Call
`ConnectivityPipeline.DocumentId` rather than reimplementing it. This is implemented in `ConnectivityPipeline.DocumentId`. See `architecture.md` for current behavior.

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

**Deletions.** Implemented in [sync-and-deletions.md](sync-and-deletions.md): connectors with a change feed report deletions via
`SourceChange.Delete`, and full runs can collect `SeenSourceRefs` for deletion by comparison.

**Checks to make first.** Search the tests for anything that assumes IDs differ between runs or documents.

## 2. Item metadata and source permissions ✅ IMPLEMENTED

**Problem.** `SourceItem` has fixed fields and `ITextExtractor.Extract` receives only a name and bytes, so a connector cannot pass
facts such as an owner, a URL or who may read the item.

**Implementation.** `SourceItem` now includes an optional metadata dictionary:

```csharp
public sealed record SourceItem(
    string Id, string DisplayName, string Extension, long SizeBytes,
    DateTimeOffset Modified, string TenantContext,
    IReadOnlyDictionary<string, string>? Metadata = null);
```

The pipeline copies metadata into `NormalisedDocument.Metadata` under the `source.` prefix. Standard keys are documented in
`SourceKeys` constants. See `architecture.md` and connector documentation for usage.

**Standard keys.** Connectors fill in what they can. Every key is optional.

| Key | Meaning |
|---|---|
| `source.url` | Link a person can open. |
| `source.container` | Folder, site, space, project or channel the item belongs to. |
| `source.owner` | Owner or author identifier. |
| `source.acl.visibility` | `private`, `restricted`, `organisation`, `public`, or `unknown`. |
| `source.acl.principals` | JSON array of user, group or role identifiers allowed to read the item. |
| `source.acl.captured` | `true` if the connector actually read permissions from the source. |
| `source.acl.capturedAt` | UTC time the permissions were read. `DocumentAccessPolicy` denies a document whose capture is older than the caller's limit (`DeniedAclStale`). |
| `source.acl.truncated` | `true` when the principal list was cut at its cap (500 by default); visibility is then `restricted` and `DocumentAccessPolicy` denies (`DeniedAclTruncated`). |

**What exists and what does not.** Built: these keys travel on every document, `SourceAcl.Captured` / `NotCaptured` write them,
and `DocumentAccessPolicy.Evaluate` is a fail-closed decision helper for consumers. Not built: no connector yet captures a
complete ACL from its provider, and nothing in this repository enforces permissions at query time; that is the consumer's job,
and the remaining work is staged in [source-permissions-and-isolation.md](source-permissions-and-isolation.md). ACL metadata
support is not permission-aware shared search.

**Rules that matter.**
- **Absence is not permission.** If `source.acl.captured` is missing or `false`, consumers must treat the item as restricted
  (`DocumentAccessPolicy` returns `DeniedAclNotCaptured`). The pipeline does not add an `IngestionReport` warning per item
  without a captured ACL; that would flag almost every document today.
- Permissions describe the source at fetch time. Re-ingest to refresh them. A revoked permission is only visible on the next run.
- Identifiers are the source system's own (an object id or email address) and are not mapped to the consumer's users. Mapping is
  the consumer's job.
- Cap the principal list (for example 500 entries) and set `source.acl.visibility` to `restricted` with a truncation flag if
  exceeded, instead of silently dropping people.
- Reading permissions often needs a broader scope than reading content. Record that for each connector in its design page, and
  make it optional so a content-only run still works.

## 3. Document transformer hook (redaction and enrichment) ✅ IMPLEMENTED

**Problem.** A document can only be changed before hashing (in the extractor) or after validation (in a wrapping exporter), neither
of which is ideal for redaction.

**Implementation.** The pipeline now supports transformers:

```csharp
public interface IDocumentTransformer
{
    NormalisedDocument Transform(NormalisedDocument document);
}
// PipelineOptions.Transformers runs them after normalisation, before validation
```

The pipeline runs transformers after normalisation and before validation, then recomputes `ContentHash` from the final body.

**Built-in `PatternRedactor`** implements opt-in rules (none on by default): email addresses, phone numbers (9+ digits, not dates),
payment card numbers (Luhn-checked), IBANs (checksum-checked), and custom regex. It replaces matches with `[REDACTED:<rule>]` in
title, body, and metadata, and records `redaction.count`. See `architecture.md` for usage.

## 4. One incremental-sync abstraction ✅ IMPLEMENTED

**Problem.** Each connector had its own sync state format and management.

**Implementation.** Fully implemented as designed in [sync-and-deletions.md](sync-and-deletions.md). `ISyncCursorStore` holds
cursors, retry lists, and versions per scope. Connectors implementing `IIncrementalSourceConnector` stream changes with
checkpoints. The pipeline saves cursors after flushing exporters. 

Key points:
- **Scope** identifies the source (e.g., `graph-drive:acme:<driveId>`)
- **Cursor** is opaque to the store
- **Commit after success** — pipeline saves only after exporters flush
- **Retry list** for failed fetches (up to `MaxRetryEntries`)
- **Reset** for expired/invalid cursors

See `architecture.md` for current behavior and `docs/done/ingestion-hardening.md` for implementation details.

## 5. REST helper ❌ NOT IMPLEMENTED

**Problem.** Web, Twitter, Reddit, YouTube and Meta each implement their own request, retry and delay logic.

**Status:** This remains unbuilt. Individual connectors continue to implement their own HTTP retry and delay logic.

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

## 6. Read-only checks for every connector ⚠️ PARTIALLY IMPLEMENTED

**Problem.** `ReadOnlyInboxGuard` checks only inbox adapters, and the calendar and sender contracts mix read and write.

**Status:** Partially addressed:
- The **conformance suite** (`SourceConnectorConformance`) includes a read-only check (check #5) that scans for write-style method
  names on connector types
- Applied to most connectors via WP7a and WP7b (see `docs/done/ingestion-hardening.md`)
- Read-only credential builders exist for some providers
- Calendar and sender contracts remain mixed (breaking change deferred)

## 7. File filters shared by file-like connectors ✅ IMPLEMENTED

**Problem.** Multiple connectors need shared file filtering logic (patterns, extensions, size limits).

**Implementation.** `FileFilterOptions` and `FileFilter` are now public in `OpenTail.Connectivity.Files` and used by folder,
Drive, and object storage connectors. Pattern semantics: `*` within segments, `**` across segments, case-insensitive, exclude
before include.

## Implementation status summary

| Section | Feature | Status |
|---|---|---|
| 1 | Stable document IDs | ✅ Implemented |
| 2 | Item metadata and source permissions | ✅ Implemented |
| 3 | Document transformer hook (redaction) | ✅ Implemented |
| 4 | Incremental-sync abstraction | ✅ Implemented |
| 5 | REST helper | ❌ Not implemented |
| 6 | Read-only checks | ⚠️ Partially (conformance suite) |
| 7 | File filters | ✅ Implemented |

## Order and size

~~1. Stable IDs (small).~~  
~~2. `SourceItem.Metadata` and the `source.*` keys (small, touches every connector's `SourceItem` calls only if they opt in).~~  
~~3. Cursor store, change stream and deletions ([sync-and-deletions.md](sync-and-deletions.md); medium, additive).~~  
~~4. Transformer hook and `PatternRedactor` (medium).~~  
5. REST helper (medium) — **Not implemented**  
~~6. Read-only scan test (small).~~ — **Partially via conformance**  
~~7. `FileFilterOptions` and `FileFilter` (small; needed before the Graph files, Drive and object storage connectors).~~

## Open questions

- Is changing the `SourceItem` constructor acceptable at 0.x, or should the metadata arrive as a separate type?
- Should `Id` include the tenant? Including it keeps tenants apart; leaving it out lets the same source be shared across tenants.
- Is the read/write contract split worth the break now, or after 1.0 planning? The Host's Email and Calendar modules consume
  `IInboxAdapter`, `IEmailSender` and `ICalendarAdapter` (and its tests implement `IInboxAdapter`), so a split must be made
  together with the Host.

Answered: the Host does not rely on random document IDs. As of `opentail-host` `3dd97bb` it uses the inbox and calendar
adapters, the email senders and `HtmlTextExtractor`, but not the pipeline, `SourceItem`, `NormalisedDocument` or the exporters, so
changes to those (sections 1 to 4) do not reach it.
