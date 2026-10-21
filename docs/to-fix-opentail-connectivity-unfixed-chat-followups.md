# OpenTail Connectivity — unresolved review follow-ups

**Scope checked:** `opentail-net/opentail-connectivity`, `main` at `7e13fe3` (21 October 2026).

This plan contains only follow-ups raised in the review conversation that still appear unfixed and are not already tracked as implementation work in the repository's existing plans. It is primarily a documentation-consistency pass; the archive-expander correctness fixes discussed in this thread are already implemented and are not repeated here.

## 1. P1 — Correct the source-reference deletion contract

**Files:**
- `docs/architecture.md`
- `docs/design/consumer-contract.md`
- `docs/design/sync-and-deletions.md` where it describes full-run deletion comparison

The architecture page currently says that a completed full run provides every discovered reference regardless of outcome, and describes `SourceRefsTruncated` as a limit-only signal. That is no longer the whole contract.

The pipeline can also mark the reference set incomplete when it cannot establish an item's references—for example, when an archive cannot be read and has no trustworthy previous manifest, or when a root item is rejected because its ID conflicts with the archive-child namespace. In those cases `SourceRefsTruncated` is true even if the maximum-reference count was not reached.

**Changes**
- Say that `SeenSourceRefs` may be used for deletion-by-absence only when the run is a completed full run, the list is present, and `SourceRefsTruncated == false`.
- Explain that `SourceRefsTruncated` means the list must not be used for deletions, whether because it hit the size cap or because references are incomplete.
- Document the relevant warning codes: `SOURCE_REFS_TRUNCATED` for a size cap and `SOURCE_REFS_INCOMPLETE` when references are unknown.
- Keep the existing rule that consumers must not infer deletions from the absence of an item in an incremental stream.

**Acceptance**
- The architecture and consumer-contract pages give the same safe deletion rule.
- No text implies that a failed item automatically makes its reference absent or that a returned list is necessarily complete.

## 2. P1 — Remove stale claims from the extension guide

**File:** `docs/extending.md`

Several statements describe features that have since been implemented as proposals or limitations:
- `SourceItem` is described as having fixed fields that cannot carry connector metadata, and the shared-groundwork design is said to propose a fix.
- Document IDs are said to change every run.
- The first three items under “Making the project easier to extend” are still presented as proposals: source metadata, stable document IDs, and a document-transformer hook.

**Changes**
- Describe `SourceItem.Metadata`, `ConnectivityPipeline.DocumentId`, and `PipelineOptions.Transformers` as existing APIs.
- Remove or rewrite the now-false claims about random IDs and metadata being unavailable.
- Keep genuinely current limitations clearly identified, including private normalisation logic, no optional DI helper package, mixed read/write inbox/calendar contracts, and the project's current API-stability policy.

**Acceptance**
- No implemented feature is listed as a proposal or described as unavailable.
- Remaining limitations are still presented honestly, rather than implying normalisation is fully extensible.

## 3. P1 — Fix the README's mixed-status design-doc section

**File:** `README.md`

The section headed **“Design proposals (nothing implemented yet)”** includes documents that now describe implemented functionality or established contracts, including shared groundwork, incremental sync/deletions, the consumer contract, testing/verification, object storage, and memory. A blanket status is misleading.

**Changes**
- Split the list into implementation/reference documents and genuinely pending designs, or provide a short status beside each link.
- Link to `docs/design/source-permissions-and-isolation.md` as a staged plan, not as completed permission-aware shared search.
- Preserve the fact that some documents contain a mixture of implemented foundations and unfinished follow-ups; do not mark an entire design fully complete just because some of it shipped.

**Acceptance**
- A README reader can tell which pages document existing code and which pages describe future work.
- The README does not suggest that all linked design documents are unimplemented.

## 4. P2 — Correct the stable-ID formula in shared-groundwork

**File:** `docs/design/shared-groundwork.md`, section “Stable document IDs”

The formula currently describes hashing `sourceType`, `tenantContext`, and `sourceRef` separated by newline characters. The implementation uses length-prefixed parts to avoid ambiguous concatenations.

**Change**
- Replace the example formula with the actual algorithm: each input part is encoded as its invariant-culture string length, a colon, and the part value; the resulting concatenation is SHA-256 hashed, and the first 32 lowercase hexadecimal characters are used.
- Keep this description aligned with `ConnectivityPipeline.DocumentId` and `docs/architecture.md`.

**Acceptance**
- The documented algorithm exactly matches the current implementation; there is no implication that newline-separated concatenation is used.

## 5. P2 — Reconcile retry defaults and report fields in the sync documentation

**Files:**
- `docs/architecture.md`
- `docs/design/sync-and-deletions.md`

The architecture text still refers to `MaxRetryEntries` with a default of 10,000 and `MaxItemAttempts` defaulting to 3. The implemented `SyncOptions` uses `MaxRetryItems = 100` and `MaxItemAttempts = 5`. The architecture text also refers to report fields `ItemsRetried` and `ItemsAbandoned`, which do not match the current report contract.

The sync design page is explicitly labelled as original design in places, but its old option names and defaults could still be copied as if current.

**Changes**
- Update the architecture's description of runtime defaults and option names to match `SyncOptions`.
- Replace outdated report-field names with the fields actually exposed by `IngestionReport`, or omit them if the report does not expose that information.
- In `sync-and-deletions.md`, either update the sample to the current implementation or label the old contract/options as historical design, prominently enough that they cannot be mistaken for the current API. Retain the existing note that retries use `FetchAsync` and `FindAsync` is not implemented.

**Acceptance**
- The runtime docs say 100 retry items and 5 attempts by default.
- No copyable example presents `MaxRetryEntries` as the current option.
- Report-field references match the actual public type.

## 6. P2 — Reconcile conformance and verification claims with the real test suite

**File:** `docs/design/testing-and-verification.md`

The page says all connectors pass the conformance suite, but its explicit coverage list names only Folder, CSV, SQLite, EML, Mbox, RSS, SiteCrawl, and social connectors. The package verification table also lists only nine “Tested” packages and omits newer packages such as SFTP, Dropbox, GCS, WebDAV, and Box, while the README labels those packages “Tested”. The approximate “about 40 test files” count is stale.

**Changes**
- Audit the actual conformance-suite fixtures/call sites and list the connectors that are demonstrably covered.
- If a connector is not covered, either add the missing explicit conformance coverage or narrow the documentation claim; do not state “all connectors” without evidence.
- Reconcile Verified/Tested package tables with the current README package table.
- Remove the approximate test-file count or update it from the current tree.
- Keep live-service verification separate from unit and stubbed-service coverage.

**Acceptance**
- Every conformance claim can be matched to a fixture/test.
- Verification labels and package lists agree across the README and testing guide.
- No stale test-count estimate remains.

## 7. P2 — Bring the object-storage design page up to date

**File:** `docs/design/object-storage.md`

The page title and package inventory describe S3, Azure Blob, and GCS, and its opening status lists S3, Azure Blob, GCS, SFTP, and Dropbox. It omits the implemented WebDAV and Box packages. It also says to start with the shared package and S3 and add other packages on demand, which conflicts with the implemented package set and `docs/storage-locations.md`.

**Changes**
- Update the page title, package table, and status paragraph to list S3, Azure Blob, SFTP, Dropbox, GCS, WebDAV, and Box as implemented adapters, with their actual verification status.
- Replace the obsolete “start with ... add the others on demand” roadmap language with a link to `docs/storage-locations.md` for implementation details and current boundaries.
- Keep the distinction between passing automated tests and verification against live provider accounts.

**Acceptance**
- The inventory matches the current README and `docs/storage-locations.md`.
- The page no longer describes implemented adapters as future additions.

## 8. P2 — Clarify what “source permissions implemented” means in the groundwork design

**File:** `docs/design/shared-groundwork.md`, section “Item metadata and source permissions”

The heading labels metadata and source permissions as fully implemented, but the delivered code provides metadata transport and a fail-closed decision helper; the existing permission plan explicitly says no connector currently provides a complete ACL for that helper and no query path is wired to enforce it. The key table also predates `source.acl.capturedAt` and does not clearly document `source.acl.truncated`.

**Changes**
- Split the status into what is implemented (metadata transport, standard keys, capture timestamp, reusable policy helper) and what remains provider-specific or consumer-side work (complete ACL capture and enforcement at the query boundary).
- Add the current `source.acl.capturedAt` and `source.acl.truncated` keys to the documented key list and explain their effect on fail-closed access decisions.
- Resolve the wording that asks for per-item `IngestionReport` warnings for uncaptured ACLs against the existing implementation note that this warning was deliberately omitted; document the actual behavior instead of implying it exists.
- Keep the implementation roadmap in `docs/design/source-permissions-and-isolation.md` as the source of truth for the remaining security work; this item is only about removing contradictory status wording.

**Acceptance**
- Readers cannot mistake ACL metadata support for complete provider ACL capture or automatic shared-search authorization.
- The documented keys and warning behavior match the current implementation and existing security plan.

---

## Explicitly not duplicated here

These were discussed or surfaced during the review, but already have a dedicated implementation plan or are fixed in the current code, so they are intentionally not repeated as new work items:

- Provider ACL capture, ACL-aware sync/versioning, freshness and end-to-end query enforcement: `docs/design/source-permissions-and-isolation.md`.
- Remaining shared-groundwork features such as the REST helper and read/write contract split: `docs/design/shared-groundwork.md` and `docs/plan.md`.
- Live-service test automation and account ownership: `docs/design/testing-and-verification.md`.
- Archive ID delimiter collision guard, shared nested-gzip byte accounting, persisted-retry/full-reset handling, aggregate nested-entry cap, manifest migration and incomplete-source-ref handling: implemented in the current archive/pipeline code; archive behavior is already documented in `docs/storage-locations.md` under ST7.

## Suggested order

1. Fix the source-reference deletion contract (item 1) because incorrect consumer guidance can cause valid indexed documents to be deleted.
2. Correct the stale extension and README status descriptions (items 2–3).
3. Fix the implementation-detail mismatches and verification claims (items 4–8).
4. Re-read the README, architecture, consumer contract, shared-groundwork, testing/verification, and object-storage pages together to make sure their status claims agree.
