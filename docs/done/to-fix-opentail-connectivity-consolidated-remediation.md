# OpenTail Connectivity — Consolidated Remediation Plan

**Review date:** 2026-10-21  
**Scope:** Consolidated from the documentation accuracy audit, bug-fix/hardening plan, and unresolved review follow-ups for `opentail-net/opentail-connectivity` `main`. Findings are based on static review; the solution has not been built or tested as part of these reviews. Confirm each finding against the current checkout before changing behaviour.

## Purpose and priorities

This document combines the overlapping review notes into one deduplicated remediation backlog. Where an item involves both implementation and documentation, fix the implementation and add regression coverage rather than weakening documentation to conceal a defect. Treat security findings as review hypotheses until confirmed against the current source and intended threat model.

Suggested order:

1. Establish a reproducible baseline: build the solution and run the existing tests.
2. Correct destructive-operation safety boundaries and source-reference deletion guidance.
3. Fix data identity/cursor correctness issues.
4. Harden crawler and feed network/parsing behavior.
5. Correct public API, design-status, and verification documentation.
6. Address tenant isolation, secret exposure, and deployment-wide policy semantics.
7. Add provider-backed integration verification and reconcile remaining docs.

## P1 — Correct source-reference deletion contract

**Areas:** `docs/architecture.md`, `docs/design/consumer-contract.md`, `docs/design/sync-and-deletions.md`

`SeenSourceRefs` is safe for deletion-by-absence only when the run is completed and full, the list is present, and `SourceRefsTruncated == false`. `SourceRefsTruncated` means the reference list must not be used for deletions whether the cause is reaching the size cap or having unknown/incomplete references. Warning codes are `SOURCE_REFS_TRUNCATED` for a size cap and `SOURCE_REFS_INCOMPLETE` when references are unknown. Consumers must never infer deletions from absence in an incremental stream.

**Work items**
- [ ] Align architecture, consumer-contract, and sync/deletion guidance on the same safe deletion rule.
- [ ] Explain both causes of `SourceRefsTruncated`.
- [ ] Do not imply a failed item automatically makes its reference absent or that a returned list is necessarily complete.

**Acceptance**
- [ ] All three pages give the same deletion rule.
- [ ] Incremental streams are explicitly excluded from deletion-by-absence.

## P1 — Make calendar writes explicitly opt-in and policy-controlled

**Relevant files:** `OpenTail.Connectivity.Inbox.Graph/GraphCalendarAdapter.cs`, `OpenTail.Connectivity.Inbox.Gmail/GmailCalendarAdapter.cs`, `docs/safety-and-credentials.md`

Graph and Gmail calendar adapters expose real write operations such as create, update, delete, and respond. Graph responses can send a reply to the organiser. The safety documentation says these operations are not guarded by a common policy; a wrapper is insufficient if callers can still obtain a raw writable adapter.

**Work items**
- [ ] Inventory every calendar operation that mutates provider state or sends an external response.
- [ ] Make read-only calendar access the default interface exposed by standard registration and examples.
- [ ] Separate read and write capabilities or expose explicit writable adapters.
- [ ] Put mutations behind an enforced policy boundary; require explicit opt-in and consider human approval for destructive actions and invitation responses in agent/automation scenarios.
- [ ] Make external responses clearly distinguishable from local-only operations.
- [ ] Update safe-use documentation and examples.

**Acceptance**
- [ ] Default registration exposes no calendar mutation methods.
- [ ] Read-only consumers cannot create, update, delete, or respond.
- [ ] Writes are denied without required policy/approval and permitted writes call the expected provider operation exactly once.
- [ ] Graph and Gmail tests cover invitation responses that send external replies.

## P1 — Apply mailbox policy to every mutation

**Relevant file:** `OpenTail.Connectivity.Inbox.Gmail/GmailEmailSender.cs`, `docs/safety-and-credentials.md`

`EmailSenderPolicy` and `PolicyEnforcingEmailSender` appear to protect sending, while delete, move, and mark-read operations can mutate mailbox state without equivalent enforcement. Gmail delete can move a message to Trash; mark-read and move operations alter labels.

**Work items**
- [ ] Inventory send, delete, move, mark-read/unread, archive, label changes, and bulk variants across providers.
- [ ] Define policy for every mutation, not only sending.
- [ ] Make read-only mailbox access the default and separate mutation capabilities behind explicit opt-in.
- [ ] Define which operations require human approval.
- [ ] Emit structured, secret-safe audit events for denials.
- [ ] Prevent policy bypass through raw provider adapters.

**Acceptance**
- [ ] With mutation policy disabled, every mutation is denied without a provider call.
- [ ] Allowed operations pass correct arguments.
- [ ] Denial and approval paths are tested for each mutation type, including provider-specific label semantics.

## P1/P2 — Prevent CSV row-ID collisions and data loss

**Relevant file:** `OpenTail.Connectivity.Csv/CsvSourceConnector.cs`

Duplicate IDs may be suffixed (`A`, `A#2`, etc.), but generated IDs may collide with original IDs. Rows with original IDs `A`, `A`, and `A#2` can yield `A`, `A#2`, and `A#2`, overwriting a row-cache entry.

**Work items**
- [ ] Trace identifier generation, cache construction, fetch-by-ID, and downstream duplicate handling.
- [ ] Guarantee uniqueness across all final IDs, including IDs already present in the CSV.
- [ ] Allocate suffixes against a set of all used final IDs, or adopt another stable collision-resistant scheme.
- [ ] Ensure a row gets the same ID across discovery runs.
- [ ] Define blank-ID and duplicate-row behavior.

**Acceptance**
- [ ] Original IDs `A`, `A`, `A#2` yield three distinct final IDs.
- [ ] Each discovered ID fetches its own physical row.
- [ ] Discovery repeats yield stable IDs.
- [ ] Blank IDs and larger duplicate groups are deterministic; no cache entry is overwritten.

## P2 — Isolate SQLite incremental-sync cursors by source configuration

**Relevant file:** `OpenTail.Connectivity.Sql/SqliteSourceConnector.cs`, `docs/architecture.md`

`SqliteSourceConnector.SyncScope` is reported to use only the data source and `TableOrQuery`. If a cursor store is shared, configurations that differ by tenant, ID field, modification field, or relevant filtering settings could incorrectly share a cursor.

**Work items**
- [ ] Confirm how `SyncScope` is formed and consumed.
- [ ] Define canonical source identity from every setting that changes cursor meaning, including source/database, table/query, ID field, modification field, tenant boundary, and relevant filters.
- [ ] Keep secrets out of scope values and logs; consider a stable hash of canonical configuration.
- [ ] Decide and document migration/reset behavior for existing cursors.
- [ ] Test independently configured connectors sharing one cursor store.

**Acceptance**
- [ ] Independent configurations have distinct cursor keys.
- [ ] Identical configurations produce the same key across restarts.
- [ ] One configuration's cursor cannot make another skip records.

## P2 — Make crawler redirect safety independent of injected `HttpClient`

**Relevant files:** `OpenTail.Connectivity.Web/SiteCrawlConnector.cs`, `OpenTail.Connectivity.Web/WebContentReader.cs`, crawler safety documentation

The default crawler client reportedly disables automatic redirects, but a caller-supplied `HttpClient` may follow redirects before the connector can validate the destination. Checking the final URI after the response is too late to prevent a request to an off-origin target. This is a conditional SSRF risk depending on client configuration and URL trust.

**Work items**
- [ ] Verify redirect behavior for default and injected clients.
- [ ] Enforce redirect policy before each hop and validate allowed schemes/origins at every step.
- [ ] If start URLs can be untrusted, define handling for loopback, private, link-local, and other non-public IPs, including DNS-rebinding considerations.
- [ ] Bound redirect count and honour cancellation/timeouts throughout the chain.
- [ ] Ensure safe behavior cannot be bypassed by an injected handler, or document strict handler requirements. For example, configure `SocketsHttpHandler` or `HttpClientHandler` with `AllowAutoRedirect = false`.
- [ ] Update both crawler documentation pages. Do not describe same-origin checks as a complete guarantee for arbitrary injected clients.

**Acceptance**
- [ ] Same-origin redirects work when allowed.
- [ ] Cross-origin redirects are blocked before the destination receives a request.
- [ ] Disallowed private/loopback/link-local targets are blocked.
- [ ] Loops and excessive chains terminate safely.
- [ ] Tests cover default and injected-client paths.

## P2 — Enforce crawler path-prefix boundaries

**Relevant file:** `OpenTail.Connectivity.Web/SiteCrawlConnector.cs`

If `WithinPathPrefixes` uses `AbsolutePath.StartsWith`, an allowed `/docs` prefix can match `/docs-private` or `/docs-admin`. Prefixes should not be treated as authorization boundaries until segment-boundary matching is enforced.

**Work items**
- [ ] Normalise prefixes consistently, including slashes and duplicate separators.
- [ ] Compare path segments rather than arbitrary string prefixes.
- [ ] Define case-sensitivity, percent-encoding, dot-segment, and Unicode normalization semantics.
- [ ] Make the matcher independently testable.

**Acceptance**
- [ ] `/docs` matches `/docs` and `/docs/page`.
- [ ] `/docs` does not match `/docs-private` or `/docs-admin`.
- [ ] Trailing slashes and encoded/normalised paths have explicit tests.

## P2 — Bound RSS/Atom response size and parser resource use

**Relevant file:** `OpenTail.Connectivity.Web/RssFeedConnector.cs`

The reported implementation downloads the whole response with `GetStringAsync` before parsing. `maxItems` limits emitted entries, not downloaded bytes or parsing work. The current RSS version token reportedly hashes GUID, link, title, description, content, and publish date, while Atom uses Atom ID plus an updated/published date token. RSS author and categories are emitted but omitted from the hash, so changes to those fields alone may leave stale metadata.

**Work items**
- [ ] Add a documented maximum response size with a conservative default.
- [ ] Stream the response and enforce the byte limit while reading; do not rely only on `Content-Length`.
- [ ] Use safe XML reader settings, including prohibited DTD processing and disabled external entity resolution.
- [ ] Bound parsing complexity, XML depth, and total entries processed where feasible.
- [ ] Honour cancellation during download and parsing.
- [ ] Return useful, non-sensitive errors for oversized and malformed feeds.
- [ ] Make version tokens cover all exported fields, then document RSS and Atom rules separately. Until fixed, document the metadata invalidation limitation and do not claim RSS uses `guid|updated`.

**Acceptance**
- [ ] Under-limit responses parse normally; over-limit responses fail without buffering the full payload.
- [ ] Missing or incorrect `Content-Length` cannot bypass the limit.
- [ ] Malformed, deeply nested, DTD/entity payloads are safely rejected or handled according to policy.
- [ ] Cancellation interrupts long downloads.
- [ ] Changes to any exported field invalidate the relevant version token.

## P1 — Correct stale extension-guide claims

**File:** `docs/extending.md`

The guide reportedly says document IDs change every run, `SourceItem` cannot carry connector metadata, and a document-transformer hook is only a proposal. The current APIs described by the review are `SourceItem.Metadata`, `ConnectivityPipeline.DocumentId`, and `PipelineOptions.Transformers`.

**Work items**
- [ ] Describe these APIs as implemented and remove false claims about random IDs and unavailable metadata.
- [ ] Explain that transformers run after normalisation and before validation/export.
- [ ] Keep genuine limitations clear: the normalisation algorithm is not replaceable, there is no optional DI helper package, inbox/calendar contracts mix read and write capabilities, and current API-stability policy applies.

**Acceptance**
- [ ] No implemented feature is described as a proposal or unavailable.
- [ ] Remaining extension limitations are explicit.

## P1 — Correct README design-document status

**File:** `README.md`

The heading “Design proposals (nothing implemented yet)” incorrectly groups pages that document shipped functionality, established contracts, and unfinished plans.

**Work items**
- [ ] Rename to “Design notes and proposals (status varies)” or split reference/implementation documents from pending designs.
- [ ] Show per-document status: implemented/reference, partly implemented, proposal/staged plan, or historical.
- [ ] Identify `docs/design/source-permissions-and-isolation.md` as a staged plan, not completed permission-aware shared search.
- [ ] Preserve mixed status where a document contains both shipped foundations and open follow-ups.

**Acceptance**
- [ ] Readers can tell what exists today versus what remains planned.
- [ ] No blanket statement says all linked design documents are unimplemented.

## P2 — Correct stable document-ID formula

**File:** `docs/design/shared-groundwork.md`, “Stable document IDs”

The design note reportedly describes newline-separated hashing, while implementation length-prefixes each input part.

**Work items**
- [ ] Document each part as its invariant-culture string length, a colon, and the part value.
- [ ] State that the concatenation is SHA-256 hashed and the first 32 lowercase hexadecimal characters are used.
- [ ] Align with `ConnectivityPipeline.DocumentId` and `docs/architecture.md`.
- [ ] Recommend calling the public helper rather than reimplementing it.

## P2 — Correct folder-root identity wording

**File:** `docs/connectors.md`

Folder IDs are based on canonical absolute file paths. Equivalent relative spellings such as `docs` and `./docs` resolve to the same root under the same working directory; moving the folder to a different absolute path changes path-based IDs.

**Work items**
- [ ] Replace the claim that any root re-spelling changes every ID.
- [ ] Clarify that identity is path-based, not content-based.

## P2 — Mark folder-sync retry issue as fixed/historical

**File:** `docs/design/sync-and-deletions.md`

The design page says failed scanned files are not retried until they change. Current folder feedback reportedly uses `ISourceItemFeedback`; `NotProcessed` removes or restores state so a later run retries the item.

**Work items**
- [ ] Mark the old issue as historical/fixed.
- [ ] Link to the current feedback contract and distinguish resolved issues from remaining limitations.

## P2 — Reconcile sync option defaults, report fields, and historical examples

**Files:** `docs/architecture.md`, `docs/design/sync-and-deletions.md`

The architecture reportedly lists `MaxRetryEntries = 10,000`, `MaxItemAttempts = 3`, and report fields `ItemsRetried` / `ItemsAbandoned`. The follow-up describes current `SyncOptions` as `MaxRetryItems = 100` and `MaxItemAttempts = 5`; verify report fields against the actual `IngestionReport` type.

**Work items**
- [ ] Update runtime option names and defaults to the current public API.
- [ ] Replace stale report-field names with actual fields or omit unsupported fields.
- [ ] Update old sync-design samples or label historical options prominently.
- [ ] Retain the note that retries use `FetchAsync` and `FindAsync` is not implemented.

**Acceptance**
- [ ] Runtime docs show 100 retry items and 5 attempts by default, if confirmed in current source.
- [ ] No copyable example presents `MaxRetryEntries` as current.
- [ ] Report-field references match the public type.

## P2 — Clarify exporter-failure behavior

**File:** `docs/architecture.md`

In non-incremental runs, non-cancellation exporter exceptions are reportedly caught and the item is reported as failed. In incremental runs, exporter failures are configured to stop and propagate so the cursor is not advanced past unflushed changes. Multiple exporters do not form a transaction: an earlier exporter may have accepted a document before a later exporter fails.

**Work items**
- [ ] Distinguish full/non-incremental reporting from incremental exception propagation.
- [ ] Explain the cursor-safety reason for propagating incremental failures.
- [ ] State that partial acceptance across exporters is possible.

## P2 — Reconcile conformance and verification claims

**File:** `docs/design/testing-and-verification.md`, plus README package table

The testing guide reportedly claims all connectors pass conformance, but its named coverage list is narrower. Package verification omits newer packages while README labels them “Tested”; the approximate test-file count is stale.

**Work items**
- [ ] Audit actual conformance fixtures and call sites; list only demonstrably covered connectors.
- [ ] Reconcile package lists/status labels across README and testing guide.
- [ ] Remove or update the approximate test-file count.
- [ ] Separate unit tests, mocked/stubbed-service coverage, and live-provider verification.

**Acceptance**
- [ ] Every conformance claim maps to a fixture/test.
- [ ] Package verification labels agree across docs.
- [ ] No stale test-count estimate remains.

## P2 — Update object-storage design inventory

**File:** `docs/design/object-storage.md`

The page reportedly omits implemented WebDAV and Box packages and retains roadmap language inconsistent with the current package set and `docs/storage-locations.md`.

**Work items**
- [ ] Update inventory/status for S3, Azure Blob, SFTP, Dropbox, GCS, WebDAV, and Box according to actual verification evidence.
- [ ] Replace obsolete “start with shared package and S3, add others later” roadmap language with a link to `docs/storage-locations.md`.
- [ ] Keep automated test status separate from live-provider verification.

## P1 — Clarify source-permission status and ACL metadata contract

**Files:** `docs/design/shared-groundwork.md`, `docs/design/source-permissions-and-isolation.md`

Metadata transport and a fail-closed decision helper do not mean provider ACL capture or query-boundary enforcement is complete. The follow-up says no connector currently provides a complete ACL for the helper and no query path enforces it.

**Work items**
- [ ] Split implemented capabilities (metadata transport, standard keys, capture timestamp, reusable policy helper) from provider-specific ACL capture and consumer-side query enforcement.
- [ ] Document `source.acl.capturedAt` and `source.acl.truncated`, including their effects on fail-closed decisions.
- [ ] Resolve wording about per-item `IngestionReport` warnings for uncaptured ACLs against the implementation note that such a warning was omitted; document actual behavior.
- [ ] Use `source-permissions-and-isolation.md` as the roadmap for remaining security work.

**Acceptance**
- [ ] Readers cannot mistake ACL metadata support for complete ACL capture or automatic shared-search authorization.
- [ ] Documented keys and warning behavior match the implementation.

## High — Enforce tenant isolation end to end

**Relevant areas:** tenant context/document metadata, exporters and persistent stores, retrieval/ranking backends, memory-governance filters

`TenantContext` reportedly labels documents but does not itself isolate access. Shared exporters, storage, caches, and retrieval layers must enforce tenant boundaries.

**Work items**
- [ ] Map where tenant identity enters and persists.
- [ ] Define authoritative tenant boundaries for reads, writes, deletes, search, and retrieval.
- [ ] Enforce tenant predicates at the storage/query boundary, not only after retrieval.
- [ ] Ensure cache, cursor, document-ID, and deduplication keys cannot merge tenants unintentionally.
- [ ] Add negative cross-tenant tests for supported backends.
- [ ] Document library guarantees versus host-application responsibilities.

**Acceptance**
- [ ] A tenant cannot retrieve, mutate, or delete another tenant's data, even when IDs overlap.
- [ ] Search and caches remain isolated under concurrent access, or multi-tenant use is explicitly disallowed without a compliant host layer.

## High — Prevent credentials leaking through settings diagnostics

**Relevant areas:** `ImapSettings`, `GmailSettings`, `GraphSettings`, configuration diagnostics/logging

Settings records may expose passwords, refresh/access tokens, client secrets, connection strings, or other secrets through generated `ToString()` output.

**Work items**
- [ ] Inspect all credential-bearing settings/configuration records.
- [ ] Redact secrets from diagnostic string representations.
- [ ] Avoid logging whole settings objects; log an explicit allowlist of safe fields.
- [ ] Add tests that representative secrets never appear in `ToString()` or supported diagnostics.
- [ ] Document safe logging practices.

**Acceptance**
- [ ] Secret values never appear in generated/custom string representations.
- [ ] Non-sensitive diagnostic fields remain available.

## Medium — Make email approval/rate limits deployment-aware

Approval/rate counters reportedly live in process memory, reset on restart, and are not shared between application instances.

**Work items**
- [ ] Confirm intended scope: per-process, user, tenant, or deployment.
- [ ] Document process-local semantics if intentional.
- [ ] If deployment-wide enforcement is required, add a pluggable shared counter/approval store with atomic updates and expiry.
- [ ] Define store-failure behavior; fail closed for safety-critical limits where appropriate.
- [ ] Test concurrency across policy instances, restarts, and store failures.

**Acceptance**
- [ ] Limits cannot be bypassed by multiple instances when shared enforcement is enabled.
- [ ] Expiry/reset and store-failure semantics are deterministic and documented.

## Medium — Warn about clear-text package credentials

**File:** `README.md`, NuGet source setup

The package setup reportedly uses `--store-password-in-clear-text` with a package token. On some platforms this is necessary, but it stores the credential in clear text in NuGet configuration.

**Work items**
- [ ] Explain the storage consequence next to the command.
- [ ] Tell users not to commit generated `NuGet.Config` files and to restrict file access.
- [ ] Prefer CI credential mechanisms or ephemeral configuration where possible.

## Medium — Add provider-backed integration verification

The README reportedly identifies several connectors as verified with unit tests or stubbed services rather than live integrations.

**Work items**
- [ ] Keep unit tests deterministic and credential-free.
- [ ] Add opt-in integration suites for Graph, Gmail, object storage, and other higher-risk connectors where feasible.
- [ ] Use dedicated test accounts/tenants and least-privilege credentials.
- [ ] Cover pagination, throttling/retries, expired credentials, permission failures, timeouts, and provider error payloads.
- [ ] Prevent mutation of production data through isolated fixtures and cleanup.
- [ ] Make the verification legend distinguish unit-tested, mocked/stubbed, and live-provider-tested behavior.

## Consolidated regression-test checklist

- [ ] Read-only calendar access cannot create, update, delete, or respond to events.
- [ ] Every mailbox mutation is denied when policy is disabled.
- [ ] CSV IDs `A`, `A`, `A#2` remain unique and map to the correct rows.
- [ ] Different SQLite connector configurations do not share an incompatible cursor.
- [ ] Off-origin redirects are blocked before the destination receives a request.
- [ ] `/docs` allows `/docs/page` but rejects `/docs-private`.
- [ ] Oversized RSS/Atom responses stop at the configured limit.
- [ ] RSS version tokens change when any exported field changes.
- [ ] Cross-tenant reads and writes are denied.
- [ ] Settings diagnostics never include secrets.
- [ ] Rate limits behave correctly across instances/restarts according to documented scope.
- [ ] Source-reference deletion is used only for complete, successful full-run reference sets.
- [ ] Stable document IDs match `ConnectivityPipeline.DocumentId`.
- [ ] Sync defaults, report fields, conformance claims, and package verification labels match the current source/test suite.

## Definition of done

A finding is complete only when:

1. The implementation is fixed or the intended limitation is explicitly documented.
2. A regression test reproduces the original failure mode and passes with the fix.
3. Relevant documentation and safe-use examples are updated.
4. The full build and relevant test suites pass.
5. Provider-specific or deployment-specific assumptions are recorded.

## Review limits

This is a consolidated static-review backlog, not a completed security audit. Re-check all findings against the current checkout and run the build/tests before treating them as confirmed. The crawler redirect concern depends on the injected client's handler configuration. Tenant isolation, credential logging, rate-limit scope, and other implementation claims should be validated against the actual public API and threat model before changes are made.
