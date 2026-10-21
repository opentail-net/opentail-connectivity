# OpenTail Connectivity — Fourth Review

**Review date:** 21 October 2026  
**Scope:** Additional static inspection of the current public `main` branch. This round focuses on core pipeline behavior and RSS/Atom feed handling. Findings below are new relative to the first three review notes; they do not repeat the earlier calendar/email safety, CSV ID, sync-scope, crawler/redirect, storage, inbox checkpoint, dedup-by-tenant, folder sync, or ICS findings.

> Static-review caveat: I have not cloned or built the repository, run its test suite, or reproduced these cases. The first finding is directly apparent from the control flow. The feed findings are based on the parser implementation and should be covered by regression tests before being treated as reproduced defects.

## Findings

### 1. P2 — Failed export can suppress later documents when content-hash deduplication is enabled

**Evidence:** [`ConnectivityPipeline.ExportAsync`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Files/Pipeline/ConnectivityPipeline.cs#L505-L535) checks `run.FirstTimeHash(doc.ContentHash)` at lines 516–519, before it calls any exporters at lines 521–530. `FirstTimeHash` immediately adds the hash to the run's seen set ([implementation](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Files/Pipeline/ConnectivityPipeline.cs#L647-L654)).

**Failure scenario:** With `DeduplicateByContentHash = true`, the first source item with body `X` reaches an exporter and that export fails. Its hash remains in the run-level seen set. A later, different source item with the same body `X` is then treated as a duplicate and skipped, even if that item could have been exported successfully. If every exporter failed for the first item, this can cause the body to be absent from all outputs for that run.

**Impact:** Valid content can be skipped because an earlier equivalent item failed to persist. This is conditional on content-hash deduplication being enabled and an export failure occurring.

**Recommended fix:** Only mark a hash as successfully handled after the required exporter operations succeed. If multiple exporters are supported, decide explicitly whether deduplication state is shared across all exporters or tracked per exporter; one exporter succeeding must not mask another exporter failing.

**Regression tests:**
- First item has body `X`; exporter throws; second item has body `X`; assert the second is attempted and can succeed.
- With two exporters, first exporter succeeds but second fails on the first item; assert the second item's duplicate body is not silently suppressed for the exporter that failed.
- Confirm ordinary successful duplicate bodies are still skipped as intended.

### 2. P2 — Atom feed parsing can choose the wrong link and uses that link as identity instead of Atom's stable ID

**Evidence:** [`RssFeedConnector.ParseAtom`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Web/RssFeedConnector.cs#L107-L140) selects the first `<link>` whose `rel` is not exactly `enclosure` (lines 111–114). It reads the Atom `<id>` at line 123, but does not use it as the returned item ID; the returned tuple uses `link` at line 131. `DiscoverAsync` then uses that tuple ID as `SourceItem.Id` and deduplicates entries by it (lines 39–44 and 59–66).

**Failure scenarios:**
- A valid entry lists a `rel="self"`, `rel="related"`, or `rel="edit"` link before its `rel="alternate"` link. The parser can select the non-content link as the entry URL.
- Two entries have the same alternate/canonical link but different Atom IDs. One is discarded by `seenIds`.
- An entry's link changes while its Atom ID remains stable. The connector produces a different source/document ID and can leave the old indexed document behind in consumers that reconcile only current IDs.

**Impact:** Wrong links in metadata, dropped Atom entries, unstable identities and potentially stale indexed documents.

**Recommended fix:** Parse Atom identity separately from display/content links. Use the Atom `<id>` as the stable source ID when present. Select `rel="alternate"` (or the Atom default when `rel` is absent) for the content link, rather than taking the first non-enclosure link. Define a deterministic fallback for malformed entries with no Atom ID.

**Regression tests:**
- `self` link before `alternate`; assert the alternate URL becomes `feed.link`.
- Two entries with the same link but distinct Atom IDs; assert both are discovered.
- Stable Atom ID with a changed alternate URL; assert the source ID remains stable.
- Entry without a `rel` attribute on its alternate link; confirm it is still accepted.

### 3. P3 — RSS content-version token omits metadata fields that the extractor emits

**Evidence:** [`RssFeedConnector.ParseRss`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Web/RssFeedConnector.cs#L74-L105) computes `versionFields` from `guid`, `link`, `title`, `description`, `content`, and `pubDate` (lines 93–95). The same entry also emits `author` and `categories` as output metadata (lines 84–92 and 96–104). Those two fields are missing from the version token.

**Failure scenario:** If a publisher changes only an item's author or categories while leaving the fields used in `versionFields` unchanged, the connector emits the old version token. When a caller enables the pipeline `VersionStore`, the pipeline can treat the item as unchanged and skip fetching/extracting/exporting it, so the changed metadata never reaches the index.

**Impact:** Stale author/category metadata for RSS items when version-based skipping is enabled. This is lower severity than content loss or access-control problems.

**Recommended fix:** Include every field that affects the emitted payload or metadata in the deterministic RSS version token, or hash a canonical serialization of the complete normalized entry payload. Keep the token stable across runs for unchanged entries.

**Regression tests:** Change only `author`; then change only `categories`; confirm each change changes `source.version` and causes the new metadata to be exported with a `VersionStore` configured.

## Suggested order

1. Fix pipeline hash handling after export success (core correctness).
2. Fix Atom ID and link-relation handling (dropped/unstable feed records).
3. Include RSS author/categories in its version token (stale metadata).

## Areas checked but not added as findings

- The pipeline intentionally finishes an archive expansion once started; this is documented in `PipelineOptions`, so I have **not** reported it as a cap bypass.
- The storage, folder and memory paths examined in this pass did not produce another sufficiently distinct, high-confidence finding worth adding without runtime tests.
