# OpenTail Connectivity — Bug Fix and Hardening Plan

**Status:** Proposed  
**Basis:** Static source and documentation review of the repository's `main` branch. The solution has not yet been built, its tests have not been run, and integrations have not been verified against live Google or Microsoft services. Confirm each finding against the current checkout before changing behaviour.

## Goals

- Make read-only access the safe default, especially when connectors are exposed to agents or automation.
- Prevent incorrect data or cursor state from being silently reused.
- Harden crawler and feed parsing against untrusted or unexpectedly large responses.
- Add regression tests so the same classes of bugs do not return.
- Make tenant isolation, secret handling, and deployment-wide policy limits explicit.

## Suggested implementation order

1. [ ] Establish a reproducible baseline: build the solution and run all existing tests.
2. [ ] Close calendar and mailbox mutation safety gaps.
3. [ ] Fix CSV identifier collisions and SQLite sync-scope collisions.
4. [ ] Harden web crawling and RSS/Atom ingestion.
5. [ ] Address tenant isolation, credential exposure, and distributed policy limits.
6. [ ] Add provider-backed integration tests and document remaining limitations.

---

## P1 — Calendar writes need an explicit safety boundary

**Finding:** Graph and Gmail calendar adapters expose operations such as `CreateEventAsync`, `UpdateEventAsync`, `DeleteEventAsync`, and `RespondToEventAsync` that can perform real writes. Graph event responses can send a response to the organiser (`SendResponse = true`). The safety documentation says these operations are not guarded by a common policy. A documented read-only wrapper is not sufficient if callers can still obtain the raw writable adapter.

**Relevant files**

- [`GraphCalendarAdapter.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Inbox.Graph/GraphCalendarAdapter.cs)
- [`GmailCalendarAdapter.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Inbox.Gmail/GmailCalendarAdapter.cs)
- [`docs/safety-and-credentials.md`](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/safety-and-credentials.md)

### Work items

- [ ] Review the public calendar interfaces and identify every operation that mutates provider state or sends an external response.
- [ ] Make read-only calendar access the default interface exposed by standard connector registration and examples.
- [ ] Separate write capabilities from read capabilities (for example, distinct read and write interfaces or explicit writable adapters).
- [ ] Put calendar mutations behind a policy-enforcing boundary; do not rely solely on callers remembering to wrap an adapter.
- [ ] Require explicit opt-in for write-enabled adapters. Consider human approval for deletes, updates, and invitation responses when used by agents or automation.
- [ ] Ensure destructive actions and invitation responses are clearly distinguishable from local-only operations.
- [ ] Update the safety documentation and examples to show the safe default and the deliberate steps needed to enable writes.

### Acceptance tests

- [ ] Default registration exposes no calendar mutation methods.
- [ ] A read-only consumer cannot invoke create, update, delete, or respond operations through its public interface.
- [ ] Writable adapter operations are denied when policy or required approval is absent.
- [ ] Each permitted write calls the expected provider operation exactly once.
- [ ] Tests cover Graph and Gmail, including invitation responses that send an external reply.

**Done when:** A caller using the documented default setup cannot accidentally perform calendar writes, and opting into writes is explicit, test-covered, and policy-controlled.

---

## P1 — Apply mailbox policy to every mutation

**Finding:** `EmailSenderPolicy` and `PolicyEnforcingEmailSender` appear to protect sending, while message deletion, marking read, and moving messages can mutate mailbox state without equivalent enforcement. In Gmail, delete can move a message to Trash, mark-read changes labels, and move changes labels/removes `INBOX`.

**Relevant files**

- [`GmailEmailSender.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Inbox.Gmail/GmailEmailSender.cs)
- [`docs/safety-and-credentials.md`](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/safety-and-credentials.md)

### Work items

- [ ] Inventory all write-capable email and mailbox methods across providers, including send, delete, move, mark-read/unread, archive, label changes, and any bulk variants.
- [ ] Define a policy model covering each mutation rather than sending alone.
- [ ] Put all mutation operations behind an enforced policy boundary.
- [ ] Make read-only mailbox access the default; expose mutation capabilities separately and require explicit opt-in.
- [ ] Define which operations require human approval and which can be permitted by policy alone.
- [ ] Ensure denials are observable through structured, secret-safe audit events.
- [ ] Update examples and documentation so policy cannot be accidentally bypassed by using a raw provider adapter.

### Acceptance tests

- [ ] With mutation policy disabled, each mailbox mutation is denied and no provider call occurs.
- [ ] Allowed operations proceed and pass the expected arguments to the provider.
- [ ] Denial and approval paths are tested for every mutation type, not just `SendAsync`.
- [ ] Tests include Gmail label semantics and other providers' equivalent operations where supported.

**Done when:** Every operation that changes mailbox state or sends mail has a consistent, tested enforcement path.

---

## P1/P2 — Prevent CSV row-ID collisions and data loss

**Finding:** Duplicate CSV IDs are suffixed (`A`, `A#2`, etc.), but generated IDs may collide with IDs that already exist in the file. For example, rows with original IDs `A`, `A`, and `A#2` can yield `A`, `A#2`, and `A#2`. If the row cache is keyed by the generated ID, one row can overwrite another.

**Relevant file**

- [`CsvSourceConnector.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Csv/CsvSourceConnector.cs)

### Work items

- [ ] Trace identifier generation, row-cache construction, fetch-by-ID behaviour, and downstream duplicate handling.
- [ ] Guarantee uniqueness across all final IDs, including IDs originally present in the CSV.
- [ ] Prefer a stable, collision-resistant identifier scheme tied to the original ID and a stable row identity; alternatively, allocate suffixes against a set of already-used final IDs until a unique ID is found.
- [ ] Ensure the same row receives the same ID across repeated discovery runs.
- [ ] Define behaviour for blank IDs and duplicate rows.
- [ ] Add regression tests before or alongside the fix.

### Acceptance tests

- [ ] The IDs `A`, `A`, `A#2` produce three distinct final IDs.
- [ ] Fetching each discovered ID returns the contents of the corresponding physical row.
- [ ] Re-running discovery yields the same final ID for each row.
- [ ] Blank IDs and larger duplicate groups are handled deterministically.
- [ ] No row-cache entry is silently overwritten.

**Done when:** Final IDs are unique and stable, and each discovered item fetches its own row.

---

## P2 — Isolate SQLite incremental-sync cursors by source configuration

**Finding:** `SqliteSourceConnector.SyncScope` is reported to be based only on the data source and `TableOrQuery`, for example:

```csharp
"sqlite:" + DataSource + "/" + TableOrQuery
```

If cursor storage is shared, two connector configurations can share a cursor even if tenant, ID field, or modification field differs. A connector may then skip records during initial ingestion or incremental sync.

**Relevant files**

- [`SqliteSourceConnector.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Sql/SqliteSourceConnector.cs)
- [`docs/architecture.md`](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/architecture.md)

### Work items

- [ ] Confirm exactly how `SyncScope` is formed and how the cursor store uses it.
- [ ] Define a canonical source identity that includes all settings that change the meaning of a cursor: source/database identity, table or query, ID field, modification field, tenant boundary, and any other relevant filtering configuration.
- [ ] Avoid including secrets in scope values or logs. Canonicalise configuration and consider a stable hash for the final scope key.
- [ ] Decide how scope changes affect existing stored cursors and document any migration/reset procedure.
- [ ] Add tests using two independently configured connectors that share one cursor store.

### Acceptance tests

- [ ] Two configurations that should be independent have distinct cursor keys.
- [ ] Identical configurations derive the same cursor key across process restarts.
- [ ] A cursor from one configuration cannot cause another configuration to skip initial records.
- [ ] Existing cursor behaviour remains correct for a single stable configuration.

**Done when:** Cursor reuse only occurs between connectors with equivalent source and sync semantics.

---

## P2 — Make crawler redirect safety independent of caller-supplied `HttpClient`

**Finding:** The default crawler client reportedly disables automatic redirects, but the public API also accepts a caller-supplied `HttpClient`. If that client's handler follows redirects automatically, checking the final URI after the response may be too late: a request to an off-origin destination may already have occurred. This is a conditional SSRF risk, depending on the supplied client's configuration and URL trust model.

**Relevant files**

- [`SiteCrawlConnector.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Web/SiteCrawlConnector.cs)
- [`WebContentReader.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Web/WebContentReader.cs)

### Work items

- [ ] Verify the current redirect behaviour for both internally created and injected HTTP clients.
- [ ] Enforce redirect policy before each redirect destination is requested; do not depend on inspecting only the final response URI.
- [ ] Define and enforce allowed schemes and origins for each hop in a redirect chain.
- [ ] If the crawler accepts untrusted start URLs, define a policy for loopback, private, link-local, and other non-public IP addresses, including DNS-rebinding considerations where applicable.
- [ ] Keep redirect count bounded and ensure cancellation/timeouts apply to the full chain.
- [ ] Document any constraints imposed on injected clients or change the API so safe redirect handling cannot be bypassed accidentally.

### Acceptance tests

- [ ] Same-origin redirect succeeds when policy allows it.
- [ ] Cross-origin redirect is blocked before the destination receives a request.
- [ ] Redirects to loopback/private/link-local addresses are blocked when disallowed.
- [ ] Redirect loops and excessive chains terminate safely.
- [ ] Tests cover the default client and the supported injected-client path.

**Done when:** Every redirect hop is validated before the next network request is made.

---

## P2 — Enforce path-prefix boundaries in the web crawler

**Finding:** `WithinPathPrefixes` reportedly uses `uri.AbsolutePath.StartsWith(prefix, ...)`. An allowed prefix `/docs` can therefore also match `/docs-private` and `/docs-admin`, which is unsafe if prefixes are intended as access boundaries.

**Relevant file**

- [`SiteCrawlConnector.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Web/SiteCrawlConnector.cs)

### Work items

- [ ] Normalise configured prefixes consistently, including leading/trailing slashes and duplicate separators.
- [ ] Compare path segments, not arbitrary string prefixes.
- [ ] Preserve or explicitly define case-sensitivity semantics for the target URL/path behaviour.
- [ ] Define how percent-encoded paths, dot segments, and Unicode paths are normalised before comparison.
- [ ] Make the prefix matcher a small independently testable function.

### Acceptance tests

- [ ] `/docs` matches `/docs` and `/docs/page`.
- [ ] `/docs` does not match `/docs-private` or `/docs-admin`.
- [ ] Trailing slash variants behave consistently.
- [ ] Encoded and normalised path cases are explicitly tested.

**Done when:** Configured path prefixes match only the intended path or its descendants.

---

## P2 — Bound RSS/Atom response size and parser resource use

**Finding:** `RssFeedConnector.DiscoverAsync` reportedly downloads the full response with `GetStringAsync` and then parses the XML. `maxItems` limits emitted items, not bytes downloaded or parsed, so a large or malicious feed may consume excessive memory/CPU.

**Relevant file**

- [`RssFeedConnector.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Web/RssFeedConnector.cs)

### Work items

- [ ] Add configurable, documented maximum response bytes with a conservative default.
- [ ] Stream the response and enforce the byte limit while reading; do not rely only on `Content-Length` because it may be absent or inaccurate.
- [ ] Configure XML parsing with safe reader settings, including prohibited DTD processing and disabled external entity resolution.
- [ ] Bound parsing complexity where feasible, including XML depth and total entries processed.
- [ ] Honour cancellation during download and parsing where supported.
- [ ] Return useful, non-sensitive errors for oversized and malformed feeds.

### Acceptance tests

- [ ] A response under the byte limit is parsed normally.
- [ ] A response above the limit fails safely without buffering the full payload.
- [ ] Missing or incorrect `Content-Length` does not bypass the byte limit.
- [ ] Malformed XML, deep XML, and entity/DTD payloads are safely rejected or handled according to policy.
- [ ] Cancellation interrupts a stalled or long download.

**Done when:** Feed size and parser work are bounded, and untrusted XML cannot trigger external entity access.

---

## High — Enforce tenant isolation end to end

**Finding:** `TenantContext` reportedly labels documents but does not itself isolate access. Shared exporters, storage, and retrieval layers must enforce tenant boundaries.

**Relevant areas**

- Tenant context and document metadata
- Exporters and persistent stores
- Retrieval and ranking backends
- Memory-governance filters

### Work items

- [ ] Map where tenant identity enters the system and where it is persisted.
- [ ] Define the authoritative tenant boundary for reads, writes, deletes, search, and retrieval.
- [ ] Enforce tenant predicates at the storage/query boundary, not only as post-retrieval filtering.
- [ ] Ensure cache keys, cursor keys, document IDs, and deduplication keys cannot merge data from different tenants unintentionally.
- [ ] Add negative cross-tenant tests for every supported storage/retrieval backend.
- [ ] Document the guarantees the library provides versus responsibilities left to the host application.

### Acceptance tests

- [ ] A tenant cannot retrieve another tenant's documents even when IDs overlap.
- [ ] A tenant cannot mutate or delete another tenant's data.
- [ ] Search results and caches remain isolated under concurrent access.

**Done when:** Tenant boundaries are enforced throughout storage and retrieval, or the library clearly prevents multi-tenant use without a compliant host layer.

---

## High — Prevent credentials from leaking through generated record logging

**Finding:** Settings records such as `ImapSettings`, `GmailSettings`, and `GraphSettings` may expose secret properties through their generated `ToString()` output. Logging a settings object can therefore disclose credentials or tokens.

**Relevant areas**

- `ImapSettings`
- `GmailSettings`
- `GraphSettings`
- Configuration diagnostics and logging

### Work items

- [ ] Inspect all settings/configuration records for passwords, refresh tokens, access tokens, client secrets, connection strings, and other sensitive fields.
- [ ] Ensure diagnostic string representations redact secrets.
- [ ] Avoid logging whole settings objects; log an explicit allowlist of safe fields instead.
- [ ] Add tests that assert representative secrets never appear in `ToString()` or supported diagnostic output.
- [ ] Document safe logging practices for consumers.

### Acceptance tests

- [ ] Secret values do not appear in generated or custom string representations.
- [ ] Expected non-sensitive diagnostic fields remain available.
- [ ] Tests include every settings type that can hold credentials.

**Done when:** Accidental logging of settings instances cannot reveal stored secrets.

---

## Medium — Make email approval/rate limits deployment-aware

**Finding:** Email approval/rate counters reportedly live in process memory. They reset on restart and are not shared between application instances, so they cannot enforce deployment-wide quotas by themselves.

### Work items

- [ ] Confirm whether current limits are intended as per-process, per-user, per-tenant, or deployment-wide limits.
- [ ] Document process-local semantics if they are intentional.
- [ ] If deployment-wide enforcement is required, introduce a pluggable shared counter/approval store with atomic updates and expiry.
- [ ] Define behaviour when the shared store is unavailable; fail closed for safety-critical limits where appropriate.
- [ ] Test concurrent requests across multiple policy instances and simulated restart/store failure.

### Acceptance tests

- [ ] Limits cannot be bypassed by creating multiple policy instances or application replicas when shared enforcement is enabled.
- [ ] Counter expiry/reset semantics are deterministic.
- [ ] Store failures follow documented fail-open/fail-closed behaviour.

**Done when:** The limits' scope and failure behaviour are explicit and tested.

---

## Medium — Add provider-backed integration verification

**Finding:** The README reportedly identifies several connectors as verified using unit tests or stubbed services rather than live-service integration tests. Mocks do not fully exercise provider-specific pagination, permissions, throttling, or real API semantics.

### Work items

- [ ] Keep unit tests deterministic and credential-free.
- [ ] Add opt-in integration-test suites for Graph, Gmail, object storage, and other higher-risk connectors where feasible.
- [ ] Use dedicated test tenants/accounts and least-privilege credentials.
- [ ] Cover pagination, throttling/retry behaviour, expired credentials, permission failures, timeouts, and provider error payloads.
- [ ] Ensure tests cannot mutate production data; use isolated fixtures and cleanup.
- [ ] Update the verification legend to distinguish unit-tested, mocked integration, and live-provider-tested behaviour.

### Acceptance tests

- [ ] CI runs unit tests on every change.
- [ ] Provider integration tests are explicitly opt-in or run in a controlled credentialed environment.
- [ ] Documentation accurately identifies which connectors have been tested against real services.

**Done when:** Verification claims reflect the evidence available for each connector.

---

## Regression-test checklist

Use this short checklist during implementation and before merging:

- [ ] Read-only calendar access cannot create, update, delete, or respond to events.
- [ ] Every mailbox mutation is denied when policy is disabled.
- [ ] CSV original IDs `A`, `A`, `A#2` remain unique and map to the correct rows.
- [ ] Two different SQLite connector configurations sharing one cursor store do not share the wrong cursor.
- [ ] Off-origin redirects are blocked before the destination receives a request.
- [ ] `/docs` allows `/docs/page` but rejects `/docs-private`.
- [ ] Oversized RSS/Atom responses are stopped at the configured limit.
- [ ] Cross-tenant reads and writes are denied.
- [ ] Settings diagnostics never include secrets.
- [ ] Rate limits behave correctly across policy instances and restarts, according to the documented scope.

## Definition of done

A finding is complete only when:

1. The implementation is fixed or the intended limitation is explicitly documented.
2. A regression test reproduces the original failure mode and passes with the fix.
3. Relevant documentation and safe-use examples are updated.
4. The full build and relevant test suites pass.
5. Any remaining provider-specific or deployment-specific assumptions are recorded.

## Scope note

This plan is based on a static review, not a completed security audit. Re-check the current source and run the existing build/tests before treating each item as confirmed. The crawler redirect issue is conditional on the `HttpClient` handler configuration; the tenant-isolation, credential-logging, and rate-limit items should also be validated against the actual public API and intended threat model before implementation.
