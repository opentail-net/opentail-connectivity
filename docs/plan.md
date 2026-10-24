# Work plan

> Repository history was restarted on 2026-10-20 and versions reset to 0.0.1. Commit hashes and version numbers in older
> documents refer to the earlier history.

What is still open across Connectivity, the Host and opentail. **Finished work moved to
[done/plan-done-2026-10-22.md](done/plan-done-2026-10-22.md)** (2026-10-22), with its history and the reasoning behind each
decision; sections whose items are all done are only there. Ideas nothing needs yet are in [optional-todo.md](optional-todo.md).

Each item names the repository it touches and links the page that explains it:

- **[C]** this repository, `opentail-connectivity`.
- **[H]** [`opentail-host`](https://github.com/opentail-net/opentail-host).
- **[O]** [`opentail`](https://github.com/opentail-net/opentail), the developer product that uses this repository.
- **[Owner]** a decision or a task for the product owner, not code. Listed in full in [with-owner.md](with-owner.md); the plan keeps
  a one-line pointer where the item belongs.

**Nothing here is work in progress.** Every open item waits on something (2026-10-23):

| Waiting on | What |
|---|---|
| The owner's decisions ([with-owner.md](with-owner.md)) | The open questions in sections 12 to 18 and 21 (notifications, data exits, adaptive-limit delays, backlog staleness, work items, audit retention and targets, triggers' repository, licence and name); the S3-compatible server for CI (31) |
| Customers | Section 5, which gates tiers 2 and 3 (section 7, frozen) and the Host memory module (M9) |
| Real accounts and systems | Live-provider suites (section 29), a Meta test account for the Graph v25.0 fields (29), Atlas for M5 (30), vendor facts (section 9), Entra ID roles (12), an external check (17), write-once storage (18) |
| Something built first | Host sign-in (section 3), and with it delegation (12) and Slack; triggers T1 to T7 (21); the review-page items G1, G2, G6 (19); the Host MongoDB module, once `OpenTail.Connectivity.MongoDb` is published (30) |
| Assessment time | M7 and M8 (section 20): no new package goes into opentail until the owner has assessed them |
| Ready to start | Refusing API keys without a profile (12) |
| Optional or deferred | Notion, Zendesk and Freshdesk, Slack front door, the model-based redactor; queue ideas in optional-todo.md; the Pinecone backend (30, parked by the owner) |

## 3. Host sign-in (prerequisite for searching as the user)

[Roadmap: what the Host's sign-in needs](roadmap.md#what-the-hosts-sign-in-needs). Tier 1 can start app-only (section 4) and
switch to per-user when this lands.

- [ ] **[H]** Delegated (per-user) consent for Microsoft and Google.
- [ ] **[H]** Per-user token storage and refresh; on-behalf-of exchange for Microsoft if the Host has its own sign-in in front.
- [ ] **[H]** Narrow scopes per tool; write scopes only for profiles that need them.
- [ ] **[H]** App-only credentials kept for shared indexes only.

## 4. Tier 1: live tools in the Host

Live search and reading, nothing copied. [Roadmap: three tiers](roadmap.md#document-search-in-three-tiers).

**Wikis, tickets and chat**
- [ ] **[H]** Notion as a module (title search, block-by-block read), if Notion is a priority.
- [ ] **[H]** Zendesk and Freshdesk profiles. *Deferred: the owner chose ServiceNow first (section 2). The builder supports both.*
- [ ] **[H]** Slack: keep the approval app and any reading app separate ([Host notes 8](host/notes.md#8-slack)). A read module
  on `assistant.search.context` with the user's token: it needs per-user sign-in (section 3) and the terms in the
  [roadmap](roadmap.md#two-ways-to-use-business-data) (nothing stored, cached or indexed).

- [ ] **[H]** Snowflake as a read-only Sql target with cost guards (warehouse checks, per-call limits, a daily budget per identity)
  ([Snowflake](https://github.com/opentail-net/opentail-host/blob/main/docs/design/snowflake.md), steps S1 to S5). *Owner,
  2026-10-23: no project needs a warehouse yet, and when one does it may be the customer's; build the capability anyway and prove
  it on an official test account. S1 and S2 built 2026-10-23 (Tested, not Verified); S5 needs a Snowflake trial account (owner).*
- [x] **[H]** More Sql targets (owner, 2026-10-23, the quick wins from a top-10 list): Fabric and Synapse through `sqlserver`
  ([guide](https://github.com/opentail-net/opentail-host/blob/main/docs/guides/fabric-synapse.md), docs only), `redshift` through
  Npgsql, and `oracle` in its own package `OpenTail.Modules.Sql.Oracle` with a CI suite on Oracle Database Free. Providers are now
  `SqlProvider` records; add-on packages declare their own. *Built 2026-10-23. Fabric, Synapse and Redshift wait on real accounts.*
- [x] **[H]** Databricks: `OpenTail.Modules.Sql.Databricks`, an ADO.NET adapter over the SQL Statement Execution API through the
  Host's guarded HTTP client (OAuth service principal or token, `:name` parameters, LIMIT, query tags, cancel on timeout)
  ([guide](https://github.com/opentail-net/opentail-host/blob/main/docs/guides/databricks.md)). *Built against a stub 2026-10-23.*
- [ ] **[Owner]** A Databricks workspace: [with-owner.md](with-owner.md#accounts-and-test-systems)

## 5. Ask the first customers

- [ ] **[Owner]** Customer questions on tier 1, and the tier 2 and 3 decision: [with-owner.md](with-owner.md#customers)

## 7. Tiers 2 and 3: indexing connectors (only if section 5 says so)

**Microsoft 365 or Google first**, per section 2:
- [ ] **[C]** Graph files, later slices: permissions and metadata-only mode, Teams channel files (OneDrive targets are built,
  with slice 1).
- [ ] **[C]** Google Drive, later slices: permissions and metadata-only mode (My Drive is built, with slice 1).

**Summary cards** (tier 2 with summaries), if section 5 says names and paths are not enough:
- [ ] **[Owner]** Summary cards' open questions: [with-owner.md](with-owner.md#open-design-questions)
- [ ] **[C]** Records, `IDocumentSummariser`, `ICardStore`, `JsonCardStore`, `SummaryCardExporter` ([summary cards](design/summary-cards.md#order-and-size)).
- [ ] **[C]** Fetch gate and end-of-run hook in the pipeline; `source.contentTag` in the connectors.
- [ ] **[H]** Summariser, search over cards with permission checks, curation screens.

## 9. Verify vendor facts

Each design lists what it depends on. Tick when checked against current vendor docs, and correct the design if needed.

- [ ] Object storage ([list](design/object-storage.md#vendor-facts-to-verify)).
- [ ] Wikis: Confluence search and sign-in; Notion's title-only search ([roadmap: wikis](roadmap.md#wikis)).
  *Partly checked 2026-10-18 against Atlassian's published OpenAPI documents (Confluence v1 `swagger.v3.json`, v2
  `openapi-v2.v3.json`): `GET /wiki/rest/api/search` (`searchByCQL`) takes `cql`, `limit`, `excerpt` (`highlight` among the
  values) and returns `results[]` with `content`, `excerpt`, `resultGlobalContainer`, `lastModified`, `url`;
  `GET /wiki/api/v2/pages/{id}` accepts `body-format=storage` and returns `body.storage.value`, `title`, `spaceId`,
  `version.number`, `version.createdAt`: the Confluence profile's paths match. Not checked: API-token sign-in on a live site,
  Notion.*
- [ ] Ticketing: Jira's current search endpoint; Zendesk and Freshdesk query syntax ([tickets](design/tickets-wikis-and-chat.md#per-system-notes)).
  *Jira checked 2026-10-18 against Atlassian's Jira Cloud v2 OpenAPI: `GET /rest/api/2/search` is marked deprecated;
  `GET /rest/api/2/search/jql` (`searchAndReconsileIssuesUsingJql`) takes `jql`, `nextPageToken`, `maxResults`, `fields` and
  returns `issues`, `nextPageToken`, `isLast`; `getIssue` and `getComments` take the parameters the Jira profile uses. Zendesk and
  Freshdesk are deferred with their profiles.*

## 11. Skills and scenarios

[Skills and scenarios](design/skills-and-scenarios.md): versioned business skills, tested against scenarios ("given artifact A
and skills X, Y, Z, these tool actions") in a separate workbench that drives a sandboxed Host. Builds on the Host's v6 plan for AI
reviewers. Implementation started 2026-10-17 in a new repository, `opentail-workbench`, plus small pieces in the Host. *Owner,
2026-10-18: keep the name, a private repository under `opentail-net`, the Host's licence (PolyForm Strict).*

*Risks raised in review (grader gaming, authoring cost, scope, the client blind spot, grade granularity, small numbers, policy
hashes, fake worlds) are recorded in the design's [risks and concerns](design/skills-and-scenarios.md#risks-and-concerns), for
the owner to weigh later. On 2026-10-17 the owner asked for implementation to start while they were away.*

- [ ] **[H]** An optional model-based redactor in the Host's export. *Deferred (owner, 2026-10-18) until a customer requires
  it; patterns plus names next to addresses until then.*

## 12. Roles and permissions

[Roles and permissions](https://github.com/opentail-net/opentail-host/blob/main/docs/design/roles-and-permissions.md) (in the Host repository): agent profiles bound to the credential, plus an
`Administers` flag, so the agents that do the business's work cannot administer how it is done. **Building next.**

*Decided by the owner on 2026-10-18:* keys without a profile get a startup warning, then are refused one release later; one
example profile per sample deployment, plus `workbench`; the three `skills.publish` recommendations are accepted; publishing
needs one approval for `staging` and two for `production`.

- [ ] **[H]** One release after profiles ship: refuse keys without a profile at startup. *Unblocked: profiles have shipped in a release.*
- [ ] **[H]** When the first customer runs Entra ID: `RoleProfiles`, app roles mapped to profiles.
- [ ] **[H]** With per-user sign-in (section 3): delegation, the profile intersected with the person's rights.
- [ ] **[Owner]** Where administration-grant notifications go: [with-owner.md](with-owner.md#open-design-questions)

## 13. Adaptive limits and intrusion detection

[Adaptive limits](https://github.com/opentail-net/opentail-host/blob/main/docs/design/adaptive-limits.md): measure what each request touched, total it per identity, and tighten in steps
(slow down, then queue, then deny only on strong evidence); only a person loosens. **The groundwork is second in line, after
profiles:** baselines need weeks of history, so the metrics must be recorded before the feature exists.

*Decided by the owner on 2026-10-18:* a short server-side delay up to a cap, then refuse with `retryAfter`; clearing Suspended
requires rotating the key; entity keys stored as salted hashes; daily summaries kept for 13 months.

- [ ] **[H]** Capacity backoff per tool or module, then per-identity budgets with incremental backoff, report-only first.
  *Capacity backoff done 2026-10-18 (host): policy `Capacity` by tool or `module.*`, a shared token bucket, a short wait up to
  `MaxDelay`, then retryable `slow_down` with a retry time; `opentail.host.backoff` telemetry. Per-identity budgets wait: they
  need a baseline from the usage rollups, which only start accruing at the first deployment.*
- [ ] **[H]** Elevated from soft signals (report-only first); decoy records; learned baselines.
  *Decoy records done 2026-10-18 (host): policy `Decoys` (markers, 8+ characters) and `Limits.Decoys`: a result containing one
  Elevated, an outbound call carrying one Suspended and (enforced) refused. Soft signals: breadth and repeated high-impact
  attempts after outside content, `Limits.SoftSignals` (report only by default), Elevated only. Volume and time-of-day signals and
  learned baselines wait for the rollups to have history.*
- [ ] **[Owner]** Adaptive limits' open questions: [with-owner.md](with-owner.md#open-design-questions)

## 14. Where data goes

[Where data goes](https://github.com/opentail-net/opentail-host/blob/main/docs/design/data-flow.md): the other half of taint. Label data where it enters, carry the labels on the session, and
check outbound calls against flow rules. Waits for sections 12 and 13.

*Decided by the owner on 2026-10-18:* five fixed labels (`finance`, `personal`, `hr`, `customer`, `secret`) plus labels
operators define; the label window reuses `TaintWindow`.

- [ ] **[Owner]** Data exits' open questions: [with-owner.md](with-owner.md#open-design-questions)

## 15. Execute with an undo window

[Undo window](https://github.com/opentail-net/opentail-host/blob/main/docs/design/undo-window.md): the action is announced, runs after a set time unless someone presses Stop, and is audited
either way. Taint still forces Queue. Waits for sections 12 and 13. *Corrected 2026-10-18: it is a `DelayFor` on `Execute`,
not a new `ToolMode` value; `ToolMode` is ordered by its numbers and frozen at contract version 1.*

*Decided by the owner on 2026-10-18:* default delay 10 minutes; anyone in the reviewer group can stop an action; stretching a
delay to working hours is an opt-in per tool.

- [ ] **[Owner]** Backlog staleness after an outage: [with-owner.md](with-owner.md#open-design-questions)

## 17. Work items and continuations

[Work items and continuations](https://github.com/opentail-net/opentail-host/blob/main/docs/design/work-and-continuations.md): an
inbox of work for agents (delegated tasks, a fresh context per run, a thread note between runs), and parking an agent's place while
it waits for an approval or an external check, resumed with the outcomes. The Host keeps the ledger and never becomes a harness.
Builds on sections 12 to 16.

*Decided by the owner on 2026-10-18:* not a harness (records and an opaque note, no transcripts, no model calls to do the work);
the Host stores the note, carrying its session's taint and labels.

- [ ] **[Owner]** Work items' open questions: [with-owner.md](with-owner.md#open-design-questions)
- [ ] **[H]** External waits: callback route and polling, against one real check.
- [ ] **[H]** MCP Tasks extension (SEP-2663) for actions and waited-on items.
  [Design](https://github.com/opentail-net/opentail-host/blob/main/docs/design/mcp-tasks.md) written 2026-10-18. It needs the C#
  MCP SDK upgraded from 1.4.1 to 2.x first (Tasks exist only on the 2026-07-28 protocol revision); the part for queued and delayed
  actions does not wait for work items. *SDK upgraded to 2.2.0 on 2026-10-18 (owner's go-ahead); old 2025-11-25 clients still
  served.* *Step 2, tasks for queued and delayed actions, built 2026-10-18 (built, unverified: the SDK's client only, no real
  client).* *Step 3, long-running calls as tasks, built 2026-10-18 (inline limit 20 s, provisional).* Step 4 (push, delegation,
  `input_required`) waits on the SDK's `subscriptions/listen` and per-user sign-in. Open: inline limit; poll intervals (60 s and 2 s
  used, provisional).
- [ ] **[Deferred]** [Slack as a front door for work](https://github.com/opentail-net/opentail-host/blob/main/docs/design/slack-front-door.md):
  not now (owner, 2026-10-18). Revisit when a Slack-based customer asks, work items exist and per-user sign-in is in place.

## 18. Audit and evidence

[Audit and evidence](https://github.com/opentail-net/opentail-host/blob/main/docs/design/audit-and-evidence.md): what a customer's
auditor will ask (what happened, how it was decided, who decided, under which rules, can the record be trusted) and the records
that answer it, mapped to SOC 2, ISO/IEC 27001, NIST SP 800-53 (AU), the EU AI Act, ISO/IEC 42001 and GDPR. Completes the
tamper-evident audit trail of section 16. Written 2026-10-18 at the owner's request.

- [ ] **[Owner]** Audit's open questions: [with-owner.md](with-owner.md#open-design-questions)
- [ ] **[H]** D8 tamper protection: streaming to the customer's log store, write-once checkpoints (S3 Object Lock first), signed
  truncation entry. *Streaming built 2026-10-18 (`audit_entry` to webhook channels; built, unverified against a real log store).
  Waiting: write-once checkpoints need a real S3 bucket with Object Lock and a packaging choice; the truncation entry waits until
  something truncates.*

## 19. Operating procedures and the gaps they found

[SOP.md](https://github.com/opentail-net/opentail-host/blob/main/OpenTail.Host/SOP.md): what the Host does by itself and what
people do, step by step, for operators, reviewers, security, integration and credential owners and auditor liaisons: three
walkthroughs (a call, a work item, a failure) and procedures for operations, reviewing, inspections, failures, changes, security
incidents, audit and personal-data requests. Written 2026-10-18 at the owner's request; every step points at a real command or
screen, and where none exists it is marked as a gap. The SOP is updated in the same commit as any behaviour it describes.

- [ ] **[H]** G1: inspections and work items on the review page; operators claim and complete inspections there.
- [ ] **[H]** G2: a chat message for operators when an inspection reaches them.
- [ ] **[H]** G6: an inspection's evidence and credential assessment on the review page (with G1).
- [ ] **[H]** G9: find actions by argument value and delete one early, for personal-data requests.
- [ ] **[H]** G12: instructions of its own for the inspector profile.
- [ ] **[H]** G15: the clock check against the checkpoint target (audit D9).

## 20. Plug-and-play memory

[Memory](design/memory.md): opentail's shared memory stack moves here, with the backend swappable (SQLite, Postgres through
`Microsoft.Extensions.VectorData`, files, hosted services), a conformance suite and a quality gate, and any agent reaching it
through the Host. Written 2026-10-19 at the owner's request, after a look at Mem0, Zep, Letta, LangMem, Cognee, the MCP memory
server, Claude's memory tool, Agent Framework context providers, and the memory poisoning and provenance papers. M1 to M8 are
treated as groundwork, like section 6; M9 is new copied data and stays frozen.

*Owner, 2026-10-23: M7 and M8 need reframing. The original opentail is being reduced in weight gradually, and the new packages
should not be entrenched in it before there has been more time to assess them. Until then the packages stay standalone; nothing
in opentail takes a dependency on them.*

- [ ] **[O]** M7: opentail consumes the package; `Core.Memory` becomes a shim. *On hold: to be reframed after assessment.*
- [ ] **[C] [O]** M8: `Connectivity.Index` becomes a document collection on the stack. *On hold, as M7.*
- [ ] **[H]** M9: the Host `memory` module (tools, resources, pushed index, recall hints, process path) and its adapters. *Frozen with tiers 2 and 3.*
- [ ] **[O]** Check opentail's own use of `SlackMemoryIngestionNormalizer` against Slack's terms ([roadmap](roadmap.md#two-ways-to-use-business-data)).

- [x] **[C]** Vector and hybrid search in the SQLite memory backend: exact similarity after the governance filter, no extension,
  per chunk, local-first up to tens of thousands of vectors ([SQLite vectors](design/sqlite-vectors.md), steps V1 to V6). *Built 2026-10-23 (6dbed20 to V6);
  measured: about 40 ms per thousand vectors, 2 s at the 50,000 advisory.*

## 21. Triggers (the *when*)

[Triggers](design/triggers.md): if this then that, governed. A rule is a trigger (schedule, webhook, Host event, connectivity
watch), typed conditions and one "then" (a Host call, a work item, a notification); agents optional. A client of the Host, not
part of it. Plug and play (scheduler, event sources, store, sink, each with a conformance suite) and built to survive a power cut
(durable firing log, idempotency keys, missed-fire and overlap policies). Proposed 2026-10-20 at the owner's request; nothing built.

- [ ] **[Owner]** Triggers' repository, licence, name and open questions: [with-owner.md](with-owner.md#choices)
- [ ] **[H]** Work created from outside (the Host's open question): an identity allowed to create work items for named profiles.
- [ ] T1 contracts; T2 durable firing log with the kill test; T3 scheduler; T4 Host sink; T5 webhooks and Host events; T6 watches;
  T7 rule management ([steps](design/triggers.md#steps)).

- Not needed yet (owner, 2026-10-22): the Postgres queue, the ratio circuit and the later research items are in
  [optional-todo.md](optional-todo.md), each with when it would be worth doing.
- [ ] **[H]** Optional `CallTimeout` for in-process Host modules, matching isolated ones.
- [ ] **[C]** Later: triggers' scheduler enqueues jobs.
- [ ] **[Owner]** Host work items on `IWorkQueue` (not moved): [with-owner.md](with-owner.md#choices)

## 29. External reviews, 2026-10-21 (to-fix documents)

Seven review documents, now in [done/](done/): the Markdown frontmatter finding, the additional bug-fix plan, the second, third
and fourth reviews, the follow-up review and the consolidated remediation plan. All fixed 2026-10-22 with regression tests,
except where noted. Breaking changes are marked **B**; the Host adopts them the next time it takes new packages.

- [ ] **[Owner]** Test accounts for the live-provider suites and for Meta's Graph v25.0 fields: [with-owner.md](with-owner.md#accounts-and-test-systems)

## 30. Redis/Valkey, Pinecone, MongoDB

[Plan](design/redis-pinecone-mongodb.md), written 2026-10-23 at the owner's request and revised the same day after an external
review. Nothing built.

- [x] **[C]** R1: the `IQueueControl` contract (store-clock durations, `RenewSlotAsync` returns bool and never revives an expired
  slot, cancellations expire everywhere) with a conformance suite. *Built 2026-10-23; fixed two SQLite bugs (renewal revived lapsed
  slots; cancellations outlived their retention when read).*
- [x] **[C]** R2 to R4: `RedisQueueControl` (Lua scripts, value compare-and-set circuits, TTLs, prefix), CI against Redis and
  Valkey, outage test. *Built 2026-10-23; two RabbitMQ gaps found by the outage test fixed with it.*
- [ ] **[C]** *Parked 2026-10-23 (owner: look again soon, do not retire).* P1 to P4: Pinecone memory backend storing what the VectorData backend stores, with the full governance mapping
  before ranking, a durable record state and settling (records with pending changes excluded inside the query) so deletes and
  ACL changes never come back during Pinecone's write lag, the
  embedding contract (model id, cosine, `MinScore`) and a namespace per tenant; Pinecone Local spike first.
- [x] **[C]** M1 to M4: `OpenTail.Connectivity.MongoDb`, one collection per query, exact operator allow-list by context (no `$facet`,
  `$regex`, `$push` in the first version), resource bounds, option checks at start-up, CI on `mongo:8` ([usage](mongodb.md)).
  *Built 2026-10-23; first built in the Host by mistake, moved to connectivity (owner).*
- [ ] **[H]** A thin Host module over `OpenTail.Connectivity.MongoDb` (agent tools, policy, audit), once the package is published.
- [ ] **[Owner]** Accounts for Atlas (M5) and Pinecone (P5): [with-owner.md](with-owner.md#accounts-and-test-systems)

## 31. External reviews, 2026-10-23 (to-fix 1 to 5)

Five review documents, now in [done/](done/) as `to-fix-2026-10-23-*.md` (1 accounts, mailboxes and social; 2 sync state and
boundaries; 3 Gmail, Mbox and DNS; 4 Host execution safeguards; 5 Host access report). All 19 findings were checked against the
code and held, and all were fixed 2026-10-23; three related gaps were found while checking (marked *found*). They fall into three classes, so each class also gets a test that
covers every implementation, not just the ones named.

*Host items built 2026-10-23 (opentail-host 6081d5b). Connectivity items built 2026-10-23 with regression tests (commits 5f802ff, 7343b69, 51bc841, c0de9b1 and the namespace commit).
Found while fixing: Gmail saved the profile's history id read after listing, so mail arriving in between was skipped (fixed with
3.1). Meta's discovery already threw, so it was never part of 1.8. Breaking for stored state: SFTP and Box root containers now
name the account, so their item ids change once.*

Classes (one test each, so new code is covered automatically):

- [x] **[C]** Identity: a multi-account test for every storage and drive connector (two accounts, same tenant: distinct
  `Container`, `SyncScope` and document ids).
- [x] **[C]** Completeness: a discovery cut short (API error, parse error, damaged manifest) marks the run's seen refs incomplete,
  never ends like a full run.
- [x] **[H]** Guards on every path: each guard (suspension, policy, flow) is swept over every path that runs an action (execute,
  retry, recovery, review edits).
- [ ] **[C]** Compatible means tested: a real-server or emulator workflow for any connector called compatible (S3 first). Waits on
  the owner's choice of server, below. Until then the S3 tests assert the request path against a stub.
- [ ] **[Owner]** The S3-compatible server for CI: [with-owner.md](with-owner.md#choices)

Order 1, wrong data acted on:

- [x] **[C]** IMAP delete, mark and move: exact Message-ID match, refuse when ambiguous, act on UIDs; *found:* `ExpungeAsync()`
  without UIDs removes every `\Deleted` message in the inbox. Attachment fetcher the same (1.2).
- [x] **[H]** Suspension checked before every run: `RunClaimedAsync`, so retries and recovery too (4.1).
- [x] **[C]** Archive manifest: a damaged manifest is not an empty one, on update and *found:* on delete (2.4).
- [x] **[C]** Reddit, Twitter, YouTube, Mbox: failures end the run incomplete, not silently; YouTube's missing stats are not
  zeros (1.8, 3.2).

Order 2, broken or leaking:

- [x] **[C]** S3: the bucket name goes to the SDK, the endpoint-qualified name stays the identity (1.1).
- [x] **[C]** Meta: the token on each request, never on a shared client's default headers (1.3).
- [x] **[C]** EML: root containment, no links, tenant check in `FetchAsync`, as the folder connector does (2.2).
- [x] **[H]** Reviewer edits: flow checks again, the preview made again for the edited arguments (4.2).

Order 3, missed or stale data:

- [x] **[C]** Gmail: fall back to a full listing only for an expired history id (404), and never save a history id past
  messages not read (3.1).
- [x] **[C]** IMAP ids: folder, UIDVALIDITY and UID when there is no Message-ID, never a random id (1.9).
- [x] **[C]** Sync scopes fingerprint everything that changes output: SQLite fields, object-storage filter (2.1).
- [x] **[C]** Memory ingestion queue: dropped and disposed items complete their callers (2.3).

Order 4 and 5:

- [x] **[C]** Account namespaces: Google My Drive, Box, SFTP (1.4 to 1.6).
- [x] **[C]** Meta Graph API default to v25.0 or later; v19.0 expired 2026-05-21 (1.7).
- [x] **[H]** GuardedHttp tries each permitted address in turn (3.3).
- [x] **[H]** The access report reads `level_suspended` (5.1).

## 32. Email attachments through the Host

Design: [design/email-attachments.md](design/email-attachments.md) (2026-10-23, revised 2026-10-24 for Transformations.Text
2.0.5.2). Built in connectivity 2026-10-24, not released: references (`ProviderMessageId`, Gmail `partId`, IMAP part
specifiers from BODYSTRUCTURE, Graph `$expand`), the new `IAttachmentFetcher` for all three providers, `AttachmentProcessor`,
the 2.0.5.2 upgrade (MailKit/MimeKit 4.18.2) and the sync-pipeline options (hidden text dropped, binary/no-OCR skipped).
Outgoing attachments are out of scope and need their own design.

- [ ] **[H]** `email.read` returns attachment objects; `email.read_attachment` (`ReadsUntrusted`); options; deferred fetcher;
  the shared OCR engine (`ConnectivityOcr.Create`) passed to the processor and to `TransformationsTextExtractor`. Needs a
  connectivity release first.
- [ ] **[C]** Live checks per provider on disposable mailboxes, including that Graph honours the nested `$select` in
  `$expand=attachments(...)` on delta.
- [ ] **[C]** Check callers of `GraphEmailSender` Delete/MarkRead/Move: do they pass the RFC id? (found while reading)
- [ ] **[Owner]** Where the tessdata folder comes from per deployment (installer or user-configured). See the design's "Upgrade impact".
- [ ] **[Transformations]** Detection gap found 2026-10-24: letter-spaced instructions with three spaces between words
  (`i g n o r e   p r e v i o u s ...`) are not flagged; with two spaces they are (Medium). Probably the collapse of 3+ spaces
  runs before the letter-spacing rule.
- [ ] **[Transformations]** Library (not blocking): protected detection by exception type; async/stream `GetModelInput`; `GetModelInput` on `IDocumentTextExtractor`.
