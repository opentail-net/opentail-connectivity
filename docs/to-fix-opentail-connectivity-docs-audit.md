# OpenTail Connectivity — documentation accuracy audit

**Review date:** 2026-10-21  
**Scope:** Current public `main` branch: README, architecture, connector reference, safety guidance, extending guide, and the design notes that those pages link to. Static comparison against the published source; no build or tests run.

## Summary

The documentation is useful and unusually candid about operational limits, but it is **not yet consistently aligned with the current source**. The biggest problem is stale guidance left in `docs/extending.md`; there are also conflicting statements about whether design documents are proposals, how stable document IDs are computed, what folder sync retries, and how RSS version tokens are generated.

Recommended order:

1. Correct the stale extension guide and the README's design-doc classification.
2. Reconcile current behavior and historical context in the design notes.
3. Tighten security setup instructions for injected HTTP clients and package credentials.
4. Correct connector-reference statements that promise more than the implementation guarantees; where the source itself is wrong, fix the implementation rather than merely softening the docs.

## Findings

### DOC-01 — `docs/extending.md` describes the pre-update API

**Priority:** High  
**Type:** Documentation is factually stale and can lead integrators to implement the wrong extension pattern.

The page says:
- document IDs change every run (line 27);
- `SourceItem` has fixed fields and permissions have to travel inside fetched bytes (lines 23–26);
- a document-transformer hook is a proposal (line 64).

The current source contradicts all three:
- `ConnectivityPipeline.DocumentId` creates a stable ID from source type, tenant and source ref using length-prefixed values;
- `SourceItem.Metadata` is copied into document metadata under `source.*` (and the connector-writing guide documents ACL-related keys);
- `PipelineOptions.Transformers` runs transformers after normalisation and before validation/export.

**Suggested fix:** Rewrite the relevant rows and limitations to describe current behavior. Remove “Document IDs change every run” and the transformer-hook proposal. Add a clear distinction between features implemented in the pipeline and constraints that remain (e.g., the normalisation algorithm itself is not replaceable).

### DOC-02 — README incorrectly labels all design docs as unimplemented proposals

**Priority:** High  
**Type:** Documentation navigation/status is misleading.

The README introduces its design links with **“Design proposals (nothing implemented yet)”**, but `docs/design/shared-groundwork.md` says its core pieces have shipped, and `docs/design/sync-and-deletions.md` explicitly says the sync core, deletions, retry list and full-run comparison are built. The memory design page also says M1–M6 are built while later milestones remain pending.

**Suggested fix:** Change the heading to something like “Design notes and proposals (status varies)” and show the current status beside each link (implemented, partly implemented, proposal, or historical reference). Do not make readers open every document to discover its state.

### DOC-03 — The stable-document-ID formula differs across documents

**Priority:** Medium  
**Type:** Contract inconsistency.

`docs/design/shared-groundwork.md` gives the implementation as `SHA-256(sourceType + "\n" + tenantContext + "\n" + sourceRef)`. The current implementation length-prefixes each component before hashing, and `docs/architecture.md` describes the components as length-prefixed.

This matters if a downstream consumer tries to recreate IDs instead of using the public `ConnectivityPipeline.DocumentId` helper.

**Suggested fix:** Make the design note describe the current algorithm accurately, or clearly label the newline formula as an abandoned historical proposal. Recommend calling `DocumentId` rather than reimplementing it.

### DOC-04 — The sync design's “problem today” section still describes a fixed folder-sync defect

**Priority:** Medium  
**Type:** Historical text reads like current behavior.

`docs/design/sync-and-deletions.md` says `FolderSyncStateStore` records every scanned file, including failed items, so failed items will not be retried until the file changes. The current folder connector implements `ISourceItemFeedback`; `NotProcessed` removes or restores state so the next run retries an item that the pipeline did not process. `docs/connectors.md` and `docs/writing-a-connector.md` describe the current retry behavior correctly.

The sync design explicitly marks the old inbox issue and long-run issue as fixed, but not the folder issue.

**Suggested fix:** Mark this statement as a historical problem that has been fixed, link to the current folder feedback contract, and distinguish the remaining limitations from resolved ones.

### DOC-05 — The architecture guide gives the wrong RSS version-token recipe

**Priority:** Medium  
**Type:** Current behavior is misstated.

`docs/architecture.md` says RSS feeds use `guid|updated` as the version token. In the current `RssFeedConnector`, RSS tokens are a truncated SHA-256 over the GUID, link, title, description, content and publish date; Atom tokens use the Atom ID plus the updated/published date token. These are materially different invalidation rules.

The current RSS hash also omits author and categories even though those metadata fields are emitted. A change only to author/categories can therefore leave stale metadata under version-based skipping.

**Suggested fix:** After fixing the implementation to include all exported fields in the version token, update the guide to state the exact behavior for RSS and Atom separately. Until the source is fixed, document the limitation rather than claiming the token is `guid|updated`.

### DOC-06 — Folder-root ID stability is described inaccurately

**Priority:** Medium  
**Type:** Connector reference overstates instability.

`docs/connectors.md` says that “moving or re-spelling the root changes every id.” The current `FolderSourceConnector` normalises the root with `Path.GetFullPath`, so path spellings such as `docs` and `./docs` resolve to the same root under the same working directory. Moving the actual folder to a different absolute path does change path-based IDs.

**Suggested fix:** Say that IDs are based on canonical absolute file paths: changing the root's actual location changes IDs; equivalent relative spellings in the same working directory are normalised. Continue to note that this is path-based identity, not content identity.

### DOC-07 — Web-crawler docs omit a critical requirement for injected `HttpClient`s

**Priority:** High  
**Type:** Security configuration is under-documented.

The `SiteCrawlConnector` source remarks explicitly state that redirect destinations are checked before each request only when automatic redirects are disabled. Its default client disables redirects, but the public constructor accepts a caller-provided client. If that client's handler auto-follows redirects, a request may be sent to the redirect target before the connector gets the final-URI check.

The connector reference currently says to configure the supplied `HttpClient`'s timeout, proxy and user agent, and the safety guide says the crawler only follows links on the start host. It does not surface the redirect-handler requirement as clearly as the WebDAV and Box examples do.

**Suggested fix:** Add a code example using `SocketsHttpHandler` or `HttpClientHandler` with `AllowAutoRedirect = false`, and explain that a caller-supplied handler must preserve this behavior. Until redirect handling is enforced by the connector itself, do not describe same-origin checks as a complete guarantee for arbitrary injected clients. Also state that path prefixes should not be treated as an authorization boundary until segment-boundary matching is enforced.

### DOC-08 — Package-install instructions persist a token in clear text without a warning

**Priority:** Medium  
**Type:** Security documentation should explain a credential-storage trade-off.

The README's NuGet source example passes `--store-password-in-clear-text` with a `read:packages` token. That flag is sometimes needed for non-Windows environments, but it means the saved NuGet configuration contains a clear-text credential.

**Suggested fix:** Explain the consequence next to the command; tell users not to commit the generated `NuGet.Config`, to restrict file access, and to prefer a CI credential mechanism or ephemeral config where possible. Keep the copy/paste setup, but do not make clear-text persistence look risk-free.

### DOC-09 — The CSV reference claims all repeated IDs are preserved, but a generated suffix can collide

**Priority:** High (data correctness)  
**Type:** Documentation currently over-promises a source behavior.

`docs/connectors.md` says repeated IDs keep every row, adding `#2`, `#3`, etc. Current `CsvSourceConnector` only counts repetitions of each original ID; it does not reserve generated IDs. With rows whose IDs are `A`, `A`, and `A#2`, the second row is assigned `A#2`, then the third row also uses `A#2`. The `_rowCache` entry is overwritten, so not every row is actually retrievable.

**Suggested fix:** Fix ID allocation in source by checking final IDs against a single used-ID set, then keep the documentation claim and add the `A`, `A`, `A#2` regression case. Do not just weaken the docs to hide this data-loss bug.

### DOC-10 — The RSS connector reference omits the unbounded feed-download behavior

**Priority:** Medium  
**Type:** Missing operational/security warning; source hardening is preferable.

The connector reference explains `maxItems`, but the implementation calls `GetStringAsync` and parses the whole XML document before applying that limit. The item count therefore does not bound response bytes or parse work.

**Suggested fix:** Add a maximum response size and bounded streaming/XML parsing in the implementation. Document the actual byte cap, XML limits and behavior on oversized feeds. Until fixed, explicitly note that `maxItems` limits emitted entries, not download size.

### DOC-11 — Pipeline exporter-failure behavior is described too generally

**Priority:** Low–Medium  
**Type:** Execution/reporting contract could be clearer.

`docs/architecture.md` says an exporter exception counts the item as failed. In non-incremental runs, the pipeline catches non-cancellation exporter exceptions and reports the item failed. In incremental runs, exporter failures are configured to stop and propagate out of the run so the cursor is not advanced past unflushed changes. The guide describes the latter later, but the earlier generic statement can imply callers always receive an `IngestionReport` with an item-level failure.

**Suggested fix:** Explicitly distinguish full/non-incremental runs from incremental runs, explain which exceptions are reported versus thrown, and note that exporters may already have accepted the document before a later exporter fails (export is not a multi-exporter transaction).

## What is already documented well

These pages currently make several important limitations clear and should be retained:

- `docs/safety-and-credentials.md` says mailbox delete/move/mark-read bypass the email policy, calendar writes have no guard, tenant context labels documents but does not isolate tenants, and config records can print secrets through `ToString()`.
- `docs/connectors.md` calls out the inbox default `AfterEnumeration` checkpoint risk and explains manual commit after downstream persistence.
- The README verification legend distinguishes real-service verification from unit/stubbed-service testing.
- `docs/writing-a-connector.md` describes the current `SourceItem.Metadata` and conservative ACL-capture contract; the extension guide needs to match it.

## Recommended documentation edits

- [ ] Rewrite `docs/extending.md` current-capabilities and proposal sections.
- [ ] Change the README's “Design proposals (nothing implemented yet)” heading and annotate per-document status.
- [ ] Align the stable ID formula in `docs/design/shared-groundwork.md` with `ConnectivityPipeline.DocumentId`.
- [ ] Mark the folder-sync issue in `docs/design/sync-and-deletions.md` as fixed/historical.
- [ ] Correct RSS/Atom version-token descriptions after implementing metadata-complete tokens.
- [ ] Fix path identity wording in `docs/connectors.md`.
- [ ] Add injected-`HttpClient` redirect guidance to both web-crawler docs; make clear whether prefixes are security boundaries.
- [ ] Add a warning to package setup about clear-text token persistence.
- [ ] Fix CSV collision handling and add the regression test before claiming all rows are preserved.
- [ ] Bound RSS input bytes/XML parsing; document the actual bounds.
- [ ] Clarify pipeline error/report behavior for incremental vs non-incremental runs.

## Review limits

This is a static comparison of the current public `main` branch and its Markdown documentation. The solution was not cloned, built, or tested in this environment. Findings about documentation/source mismatch are based on visible source and guide text; implementation behavior should still be verified by the repository's test suite before release.
