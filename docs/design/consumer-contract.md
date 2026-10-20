# Design: what downstream stores can rely on

**Status:** proposal. It describes the contract that the other designs would create, so consumers can plan for it. Today most of
it is *not* true; see [extending.md](../extending.md) for the current state.

## Why this matters

Connectors produce documents, but the value is in the store, search index or application that receives them. That consumer needs
to know which fields are stable, how to apply updates and deletions, and how to treat permissions. If this is vague, every
consumer guesses differently, and guessing wrong on permissions leaks data.

## The contract

| Topic | Guarantee after the shared-groundwork changes | Today |
|---|---|---|
| Identity | `Id` is stable for a given source type, tenant and `SourceRef`. | Random per run. |
| Change detection | `ContentHash` changes when the body changes. | True (hash of the trimmed body). |
| Updates | Processing the same item again is safe (idempotent upsert by `Id`). | Needs `SourceRef` as key. |
| Ordering | None. Items arrive in any order. Do not rely on sequence. | None. |
| Delivery | At least once. Duplicates are possible after a failed run. | At most once for incremental runs: folder state records failed files, and inbox adapters save their cursor before the caller has processed the messages. Full runs repeat everything. |
| Deletions | Reported only by connectors that can (delta feeds), through `IDeletionExporter`, or by a complete full run's `SeenSourceRefs` (with `CollectSourceRefs`). Never inferred from absence in an incremental run. See [sync-and-deletions.md](sync-and-deletions.md). | Not reported. |
| Permissions | `source.acl.captured=true` means permissions were read from the source. Anything else means unknown, which must be treated as restricted. | Not available. |
| Tenant | `TenantContext` labels the owner of the data. It is not enforced anywhere. | Same. |
| Metadata | `source.*`, `extraction.*` (connector keys such as `extraction.email.subject`), `run.*`. Values are strings. | Same, without ACL keys. `InboxEmailDocumentMapper` writes `email.*` without the prefix. |
| Redaction | If a transformer is configured, the stored body, title and metadata are already redacted, and the hash is of the redacted text. | Not available. |

## What a consumer should do

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

- Metadata keys are additive. New keys can appear; existing meanings do not change within a minor version.
- Consumers should ignore keys they do not know.
- A change to how `Id` is built is a breaking change and is announced in the changelog.
- The exported JSON shape (JSONL, report) follows the same rules. A schema version field on exporters is worth adding before 1.0.

## Open questions

- Should the contract be enforced by tests in this repository (a conformance test every connector must pass)?
- Should the JSONL exporter carry a `schemaVersion`?
