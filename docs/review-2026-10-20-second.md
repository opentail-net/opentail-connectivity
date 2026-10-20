# External code review, second pass (2026-10-20): findings and fixes

ChatGPT reviewed `main` again at `1cc3b40`, reading the source without running anything. It confirmed that the fixes from the
[first review](review-2026-10-20.md) hold: memory scope, redaction, missing ACL, vector upsert and delete, folder state,
Reddit, SQLite read-only and the direct-send rate limit. It reported seven issues. Each one was checked against the code.
**All seven hold.** Two of them are still-open parts of E2 from the first review. One is a mistake in my own first-review fix
(S6). All seven were fixed on 2026-10-20 (plan section 23).

None of these classes is used by the Host yet, so this remains library-level exposure.

## Order of work

| # | Item | Severity | Notes |
|---|------|----------|-------|
| S1 | SQLite incremental cursor can stop making progress | P1 integrity (**raised** from P2) | livelock, not just repeated work |
| S2 | Approval: double send, swallowed persistence, no rate slot or policy re-check at send | P2 | this is E2 from the first review, unchanged |
| S3 | File symlinks escape the folder root | P2 boundary | the gap the first review accepted |
| S4 | Crawler redirect boundary is checked after the request | P2 boundary | documented, not enforced |
| S5 | Memory request filters don't really narrow | P3 correctness | my comment claims they do |
| S6 | SQLite discovery advertises null-id rows that fetch can't read | P3 | my B7 advice caused it |

Each fix starts with its regression test, which must fail first, as before.

## S1. SQLite incremental cursor can stop making progress *(confirmed; worse than reported)*

`OpenTail.Connectivity.Sql/SqliteSourceConnector.cs`: the checkpoint holds only the last modified value (`KeysetCursor` stores
`lastMod`). A resumed run starts at `modified >= @m`, so it re-reads every row that shares the checkpointed value. The comment
says this is deliberate, so rows are repeated rather than missed.

The pipeline makes that a livelock (`ConnectivityPipeline.cs` ~95-125). When `MaxItemsProcessed` is reached, it stops and
**commits** the latest checkpoint. Say N rows share one modified value and the cap is smaller than N (bulk imports and
date-only columns make this common). Every run then commits the same cursor, re-reads the same first rows, hits the cap again
and never gets past that group. Without a cap, the same group is still reprocessed on every resume.

**Fix.**
- Store both values in the cursor: `{type, text, idType, idText}`, keeping each SQLite type as `KeysetCursor` already does for
  the modified value.
- Resume strictly after the pair, with the same query the in-run paging uses:
  `WHERE (mod > @m OR (mod = @m AND id > @i)) AND id IS NOT NULL`. Drop the `>=` resume path for new cursors.
- An old cursor without an id still resumes with `>=` (a compatible read), and the next checkpoint writes the new form.
- Write down in the class remarks that `IdField` must be unique, because it is already the item id. With duplicate ids, a
  strict resume could skip rows.

**Trade-off taken (2026-10-20):** a strict resume misses a row committed *after* the checkpoint with exactly the checkpointed
modified value and a lower id. Only a coarse column (dates, whole seconds) makes that likely, and the class remarks say so.
Re-reading the whole tie group on every resume, the alternative, is the livelock itself and costs a full group per run after
a bulk import.

**Tests:** five rows with the same modified value, batch size 1, `MaxItemsProcessed = 1`, and a real `FileSyncCursorStore`.
Five consecutive pipeline runs reach all five rows, each exactly once. An old-format cursor still resumes, and is rewritten in
the new form.

## S2. Email approval *(confirmed; this is E2)*

Everything the review reports is already in [E2](review-2026-10-20.md#e2-double-send-on-approval-silent-persistence):
- `ApproveAsync` checks `pending`, sends, then updates the status, so it can send twice.
- `InMemoryPendingSendStore` swallows persistence failures.
- Approval sends go through the raw sender, so they skip the rate limit and the recipient policy.

Queueing only calls `EnsureWithinLimits()`, which doesn't take a slot, so the approval path is not rate-limited at the moment
of sending. The E2 plan stands as written:
- a compare-and-set claim, `pending → sending`;
- re-check recipient policy and **reserve a slot through the same `IEmailRateLimiter`** at approval time;
- an `unknown` state for ambiguous provider outcomes;
- persistence errors that are visible;
- `AtomicFile` for the store file;
- status writes after the send use `CancellationToken.None`.

One addition: `EmailSendApprovalService` takes an `IEmailRateLimiter` and an `EmailSenderPolicy`, both optional, so existing
calls still compile. Approvals and direct sends must share one limiter instance for the cap to mean anything. Say so in both
classes' docs.

## S3. File symlinks escape the folder root *(confirmed)*

`FolderSourceConnector.cs`: directory links are skipped, but `Directory.GetFiles` still returns file symlinks. `FetchAsync`
checks the link's own path, which is inside the root, and `File.ReadAllBytesAsync` then follows the link outside it. The first
review left this open on purpose, and it should be closed.

**Fix.** In enumeration, skip files whose `new FileInfo(path).LinkTarget` is not null (symlinks and junction-style links;
OneDrive placeholders have no link target, so they stay). In `FetchAsync`, also refuse a path whose
`File.ResolveLinkTarget(path, returnFinalTarget: true)` is not null. That catches an item id handed in from elsewhere, and a
link created after discovery. No option to follow links for now. Hard links can't be told apart from ordinary files: say so
in the remarks.
**Test:** a supported file inside the root that is a symlink to a file outside it. It isn't discovered, and `FetchAsync` on
it throws `UnauthorizedAccessException`. Skip the test when the OS refuses to create the symlink, as the B4 test does.

## S4. Crawler redirect boundary is checked after the request *(confirmed)*

`SiteCrawlConnector.cs` checks `response.RequestMessage.RequestUri` after `GetAsync`. If the `HttpClient`'s handler follows
redirects (the default), the off-origin request has already been sent, possibly to an internal address. The class remarks
say so, so this is documented rather than enforced. `SitemapParser` and `RobotsTxtParser` behave the same way.

The connector can't change a handler it was given, and .NET has no per-request switch to stop redirects. So:

**Fix.**
- Add `SiteCrawlConnector.CreateHttpClient(TimeSpan? timeout = null)`. It returns a client on a `SocketsHttpHandler` with
  `AllowAutoRedirect = false`. Add a constructor overload without an `HttpClient` that uses it and disposes it.
- One internal `SendWithinOriginAsync(uri)` used by page fetch, `FetchAsync`, sitemap and robots. On a 3xx it reads
  `Location`, resolves it against the current URI, checks the origin (same rule as `HasSameOrigin`, including the https
  upgrade), and follows at most 5 hops. An off-origin hop is dropped *before* it is requested.
- With a caller-supplied client that still auto-redirects, the after-the-fact check stays as a backstop. The remarks say the
  boundary is enforced only with `CreateHttpClient()` or a handler with redirects off. The Host must use `CreateHttpClient()`
  when it exposes crawling.

**Tests:** a stub handler that records every requested URI. It answers the start page with `302 Location: http://internal/`.
The recorded URIs never include `internal`, for page fetch, sitemap and robots. A same-origin 302 is followed. A chain longer
than 5 hops stops.

## S5. Memory request filters don't really narrow *(confirmed)*

`MemoryRetriever.BuildFilter`: `FromUtc`, `ToUtc`, `Workspace`, `ProjectScope` and `RepositoryScope` use
`filter.X ?? request.X`. If the access filter has a value, the request's value is ignored. A request for a narrower time
window, or a different workspace, gets the access filter's wider or different results. This isn't a permission leak, because
the caller only gets what the access filter allows. But it breaks the contract, and the comment I wrote there says each field
"takes the stricter of the two", which is false.

**Fix.**
- Time window: `FromUtc` takes the later bound and `ToUtc` the earlier one. If they cross, there are no results.
- Workspace, project and repository: if both are set and differ (trimmed, case-insensitive), return no results with the
  withheld reason `"scope-conflict"`. If only one is set, use it.
- Correct the comment.

**Tests:**
- Access window January–December with a request for March: only March records.
- A request window crossing the access window: none.
- Access workspace `a` with a request for `b`: none, and `scope-conflict` is counted.
- Access with no workspace and a request for `b`: only `b`.

## S6. SQLite discovery advertises null-id rows that fetch can't read *(confirmed; my advice caused it)*

B7 gave null-id rows the id `row:<n>`, copying the CSV connector. But `FetchAsync` reads the row back with
`WHERE IdField = @id`, which can never match a NULL id. The row is discovered, fetched as empty bytes, and never ingested. The
CSV connector doesn't have this problem because it has no fetch by id. A row number is also not a durable identity for a query
whose order or contents change.

**Fix.** Skip null-id rows in discovery too, as the incremental path already does. That removes `row:<n>`. Say in the class
remarks that rows without an id are not read. Delete the now-dead `Guid.NewGuid()` fallback in `Item()`.
**Test:** a table with one null-id row and one normal row. Discovery returns only the normal row, and a full pipeline run
ingests it with non-empty content. Replace the B7 test that asserted `row:1`.

## Also corrected

The first review document's release note said `NotApplicable`. The shipped name is `MemorySourceAcl.OrganisationWide`.

## Suggested batches

- **Me:** S1 and S2. S1 changes the cursor format and needs the pipeline-level test. S2 is the concurrency design.
- **Delegable:** S3, S4, S5 and S6. Each is local and fully specified above, and S4 is the largest.

## Release note items (with the first review's)

- `IPendingSendStore.TryTransitionAsync` and `IEmailSendApprovalService.RequeueAsync` are new interface members: other
  implementations must add them. New statuses `sending` and `unknown` (`PendingSendStatus`). `InMemoryPendingSendStore` now
  throws on a failed save and on an unreadable file, which it moves aside.
- SQLite incremental cursors gain the id. Old cursors are read once and rewritten.
- `SiteCrawlConnector` is `IDisposable` and has `CreateHttpClient()`. Use either one for the redirect boundary to hold.
- `EmailSendApprovalService` requires a policy, and a limiter when the policy caps sends; build it with
  `PolicyEnforcingEmailSender.CreateApprovalService()`. `MemoryRedaction.Redact` takes the disposition, and a redacted hit's
  record keeps only allowlisted metadata.

## Third pass (2026-10-20, at `af4da2d`): what to apply and what not

ChatGPT checked the fixes again. Each point was weighed against the code rather than applied as written.

| # | Finding | Verdict | What to do |
|---|---------|---------|------------|
| T1 | Approval service runs without policy or limiter when constructed with two arguments | **Valid** | Make wiring hard to get wrong (below), not just fail construction |
| T2 | Redirects can leave `AllowedPathPrefixes` | **Valid, P3** | Check page redirects against the prefixes too |
| T3 | Empty-string ids skipped by discovery, read by incremental sync | **Valid, P3** | One rule in both modes: `AND id <> ''` |
| T4 | Redacted records keep source metadata (URL, actor, labels) | **Partly valid** | Allowlist for *Redacted* hits only, not *Summarized* ones |
| T5 | The cursor can miss late rows with coarse timestamps | Already documented | One line in the docs: an integer change counter works best |
| T6 | CI status for `af4da2d` unverified | **Not an issue** | CI passed on Windows and Ubuntu (checked after the push) |

**T1.** The point stands: `new EmailSendApprovalService(store, sender)` keeps the double-send fix but skips the recipient rules
and the cap. Failing construction when the policy has caps (the suggestion) still allows a *separate* limiter, which splits the
budget, the very mistake it is meant to stop. `PolicyEnforcingEmailSender` already holds everything the approval service needs:
the raw sender, the policy, the store and the limiter. So:
- Add `PolicyEnforcingEmailSender.CreateApprovalService()`, which builds the service from those same instances. That is the
  documented production path.
- Make `policy` a required constructor parameter of `EmailSendApprovalService` (breaking, before 0.3.0 is tagged). If the
  policy sets an hourly or daily cap and no limiter is passed, throw `ArgumentException`.
- Wiring test: one direct send plus one approval against `MaxEmailsPerHour = 1` refuses the second, in either order.
- The single-process limit is already in `InMemoryPendingSendStore`'s remarks. Repeat it in the service's remarks: several
  processes approving need a store whose `TryTransitionAsync` is atomic across them, such as a database.

**T2.** `AllowedPathPrefixes` filters discovered links and sitemap entries, but a same-origin redirect from `/docs/a` to
`/admin/x` is followed, and its content is stored under `/docs/a`. The option's name promises a fetch scope, so enforce it:
give `GetWithinOriginAsync` an optional path predicate, and use it for page fetches and `FetchAsync`, not for sitemap and
robots, whose own paths sit outside any page prefix. Test: `/docs/a` → 302 → `/admin/x` is never requested.

**T3.** `Text('')` is `""`, not null, so incremental sync emits an item with an empty id. Fetch can even read it back. But two
such rows would collide, and the class remarks say empty ids are not read in either mode. Add `AND {id} <> ''` to the three
incremental queries (an integer id compares unequal to `''`, so integer ids are unaffected). Test: a row with `''` and a
normal row, through incremental sync, gives only the normal row. Whitespace-only ids stay as they are: they are real values.

**T4.** Keeping `Source` on a redacted record was deliberate, for citation, but nothing defined which fields are safe. A URL
can carry a signed token or a customer name, and labels and entity refs can say as much as the text. However, the reviewer's
fix would be wrong for *Summarized* hits. Those are records the caller may see at their sensitivity, with only the raw chunk
hidden, so stripping their metadata would take away information the caller is entitled to. So:
- `MemoryRedaction.Redact` takes the disposition. For **Redacted** (sensitivity above the caller), keep an allowlist: `Id`,
  `Kind`, `Policy`, timestamps, `Collection`, `SchemaVersion`, the superseded and derived flags and ids, and from `Source`
  only `SourceType`, `SourceSystem`, `DocumentId`, `Workspace`, `ProjectScope`, `RepositoryScope` and `Acl` (as built: the ACL
  without its principals, since group names can be telling too). Everything else
  is cleared, including `Url`, `ExternalId`, `ThreadId`, `ConversationId`, `Actor`, `Writer`, `Labels`, `EntityRefs`, `Span`,
  `ObservedBy`, `CreatedBy`, `UpdatedBy` and `DeletedReason`.
- For **Summarized**, keep today's behaviour: only the text and what is derived from it go.
- Document the allowlist on `MemoryHit`. Test: a Secret record with a marker in its URL, actor, labels and span label
  serialises with no marker. A Summarized hit keeps its URL.

**T5.** Agreed, and already in the class remarks. Add that an integer change counter maintained by the database (a
`rowversion`-style column) as `ModifiedField` avoids the gap entirely, because it pages as a number. No code change.
