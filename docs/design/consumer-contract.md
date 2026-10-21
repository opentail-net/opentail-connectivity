# Design: what downstream stores can rely on

**Status:** implemented (plan section 26). This was a proposal; the contract is now established and stable. See
[docs/done/ingestion-hardening.md](../done/ingestion-hardening.md) for implementation details.

## Implementation Status Summary

All major contract elements are implemented and stable:

| Feature | Status | Notes |
|---|---|---|
| Stable document IDs | ✅ Implemented | SHA-256 derived from source type, tenant, and source ref |
| Metadata and permissions | ✅ Implemented | `source.*`, `extraction.*`, `run.*` keys; ACL support |
| Document transformers | ✅ Implemented | Redaction/enrichment via `IDocumentTransformer` |
| Incremental sync | ✅ Implemented | Cursor-based with flush-before-commit guarantee |
| Deletions | ✅ Implemented | Via `IDeletionExporter` and `SeenSourceRefs` collection |
| Retry handling | ✅ Implemented | Retry list with configurable max attempts |
| Version-based skipping | ✅ Implemented | ETag/version token support via `IItemVersionStore` |
| Conformance testing | ✅ Implemented | All connectors pass `SourceConnectorConformance` suite |
| Verification labels | ✅ Implemented | All packages labeled "Verified" or "Tested" |
| JSONL schema version | ❌ Not implemented | Worth adding before 1.0 |

## Why this matters

Connectors produce documents, but the value is in the store, search index or application that receives them. That consumer needs
to know which fields are stable, how to apply updates and deletions, and how to treat permissions. If this is vague, every
consumer guesses differently, and guessing wrong on permissions leaks data.

## The contract

| Topic | Guarantee | Status |
|---|---|---|
| Identity | `Id` is stable for a given source type, tenant and `SourceRef`. Derived via SHA-256 hash: same item = same ID across runs. | ✅ Implemented |
| Change detection | `ContentHash` changes when the body changes (hash of trimmed body). | ✅ Implemented |
| Updates | Processing the same item again is safe (idempotent upsert by `Id`). | ✅ Implemented |
| Ordering | None. Items arrive in any order. Do not rely on sequence. | ✅ Implemented |
| Delivery | At least once. Duplicates are possible after a failed run. Failed items are retried (see retry list). | ✅ Implemented |
| Deletions | Reported via `SourceChange.Delete` through `IDeletionExporter`, or by comparing a full run's `SeenSourceRefs` list (with `CollectSourceRefs`). Never inferred from absence in an incremental run. See [sync-and-deletions.md](sync-and-deletions.md). | ✅ Implemented |
| Permissions | `source.acl.captured=true` means permissions were read from the source. Anything else means unknown, which must be treated as restricted. | ✅ Implemented |
| Tenant | `TenantContext` labels the owner of the data. It is not enforced anywhere. | ✅ Implemented |
| Metadata | `source.*` (connector metadata including ACL), `extraction.*` (connector-specific keys like `extraction.email.subject`), `run.*` (ingestion metadata). Values are strings. | ✅ Implemented |
| Redaction | If a transformer is configured via `PipelineOptions.Transformers`, the stored body, title and metadata are already redacted, and the hash is of the redacted text. | ✅ Implemented |
| Version skipping | When `source.version` is present and unchanged (ETag, version token), fetch/extraction/export are skipped. Requires `IItemVersionStore` configured. | ✅ Implemented |
| Retry handling | Failed fetches are retried up to `MaxItemAttempts` (default 5) via a stored retry list. Permanent failures are reported without blocking the cursor. | ✅ Implemented |

## What a consumer should do

> **Shared-indexing status:** this library transports ACL metadata but does not enforce tenant isolation or provide complete
> permission capture for Graph, Google Drive or generic object storage. Follow the staged [source permissions and isolation
> plan](source-permissions-and-isolation.md) before using these connectors for per-user shared search.

1. **Upsert by `Id`.** Replace the stored document when `ContentHash` differs; ignore it when it matches.
2. **Keep `SourceRef` and `TenantContext`** alongside the `Id`, so records can be traced back to the source and removed per tenant.
3. **Apply deletions deliberately.** Remove documents only on an explicit deletion list, or after a full run whose
   `SeenSourceRefs` list does not include them (and is not truncated). Never delete because an incremental run did not mention
   something.
4. **Filter at query time by permissions.** Check `source.acl.captured` and `source.acl.principals` against the asking user's
   identity. Resolve groups on your side. If `captured` is not `true`, show the document only to people already trusted with the
   whole source. This is the rule that prevents leaks.
5. **Re-ingest to refresh permissions.** A change in the source is not visible until the next run reads it. Choose a schedule that
   matches how fast access changes matter to you.
6. **Treat content as untrusted.** Especially when the text reaches a language model, which can follow instructions embedded in
   an email, web page or ticket.
7. **Protect the store like the source.** It holds copies of the same sensitive data.

## Versioning

- Metadata keys are additive. New keys can appear; existing meanings do not change within a minor version. ✅ **Established pattern**
- Consumers should ignore keys they do not know. ✅ **Established pattern**
- A change to how `Id` is built is a breaking change and is announced in the changelog. ✅ **Established pattern**
- The exported JSON shape (JSONL, report) follows the same rules. ⚠️ **Partial:** A schema version field on exporters is worth adding before 1.0 but not yet implemented.

## Open questions

- ✅ **Resolved:** Conformance tests are implemented. Every connector passes the `SourceConnectorConformance` suite (WP7 from ingestion-hardening).
- Should the JSONL exporter carry a `schemaVersion` field? (Not yet implemented.)
