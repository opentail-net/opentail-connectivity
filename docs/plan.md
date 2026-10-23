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
- **[Owner]** a decision or a task for the product owner, not code.

**Nothing here is work in progress.** Every open item waits on something (2026-10-22):

| Waiting on | What |
|---|---|
| The owner's decisions | The open questions in sections 12 to 18 and 21 (notifications, data exits, adaptive-limit delays, backlog staleness, work items, audit retention and targets, triggers' repository, licence and name) |
| Customers | Section 5, which gates tiers 2 and 3 (section 7, frozen) and the Host memory module (M9) |
| Real accounts and systems | Live-provider suites (section 29), vendor facts (section 9), Entra ID roles (12), an external check (17), write-once storage (18) |
| Something built first | Host sign-in (section 3), and with it delegation (12) and Slack; triggers T1 to T7 (21); the review-page items G1, G2, G6 (19) |
| Assessment time | M7 and M8 (section 20): no new package goes into opentail until the owner has assessed them |
| Ready to start | Refusing API keys without a profile (12) |
| Optional or deferred | Notion, Zendesk and Freshdesk, Slack front door, the model-based redactor; queue ideas in optional-todo.md |

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

## 5. Ask the first customers

- [ ] **[Owner]** Where does tier 1 fall short? Which collections are searched most, and does keyword search find what people need?
- [ ] **[Owner]** Decide from the answers whether tiers 2 and 3 are needed, and for which suite first. **Sections 6 and 7 wait on
  this.** *Owner, 2026-10-18: tiers 2 and 3 stay frozen until customers answer; no index nobody asked for.*

## 7. Tiers 2 and 3: indexing connectors (only if section 5 says so)

**Microsoft 365 or Google first**, per section 2:
- [ ] **[C]** Graph files, later slices: permissions and metadata-only mode, Teams channel files (OneDrive targets are built,
  with slice 1).
- [ ] **[C]** Google Drive, later slices: permissions and metadata-only mode (My Drive is built, with slice 1).

**Summary cards** (tier 2 with summaries), if section 5 says names and paths are not enough:
- [ ] **[Owner]** Decide the summary language, who owns the tag vocabulary, and where cards live
  ([summary cards](design/summary-cards.md#open-questions)).
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
- [ ] **[Owner]** Still open: where administration-grant notifications go.

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
- [ ] **[Owner]** Still open: delays for interactive agents, who is notified, baselines per key or per profile.

## 14. Where data goes

[Where data goes](https://github.com/opentail-net/opentail-host/blob/main/docs/design/data-flow.md): the other half of taint. Label data where it enters, carry the labels on the session, and
check outbound calls against flow rules. Waits for sections 12 and 13.

*Decided by the owner on 2026-10-18:* five fixed labels (`finance`, `personal`, `hr`, `customer`, `secret`) plus labels
operators define; the label window reuses `TaintWindow`.

- [ ] **[Owner]** Still open: which tools count as an exit, value-matching cost and privacy, regions.

## 15. Execute with an undo window

[Undo window](https://github.com/opentail-net/opentail-host/blob/main/docs/design/undo-window.md): the action is announced, runs after a set time unless someone presses Stop, and is audited
either way. Taint still forces Queue. Waits for sections 12 and 13. *Corrected 2026-10-18: it is a `DelayFor` on `Execute`,
not a new `ToolMode` value; `ToolMode` is ordered by its numbers and frozen at contract version 1.*

*Decided by the owner on 2026-10-18:* default delay 10 minutes; anyone in the reviewer group can stop an action; stretching a
delay to working hours is an opt-in per tool.

- [ ] **[Owner]** Still open: how stale a backlog may be after an outage before it is held. *Built with the proposal (more than its
  delay late); change it if you prefer another threshold.*

## 17. Work items and continuations

[Work items and continuations](https://github.com/opentail-net/opentail-host/blob/main/docs/design/work-and-continuations.md): an
inbox of work for agents (delegated tasks, a fresh context per run, a thread note between runs), and parking an agent's place while
it waits for an approval or an external check, resumed with the outcomes. The Host keeps the ledger and never becomes a harness.
Builds on sections 12 to 16.

*Decided by the owner on 2026-10-18:* not a harness (records and an opaque note, no transcripts, no model calls to do the work);
the Host stores the note, carrying its session's taint and labels.

- [ ] **[Owner]** Discuss the design; settle the open questions (note sizes, outside creation of work, `MayDelegateTo` and depth,
  default ttl, which external check first).
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

- [ ] **[Owner]** Settle the open questions: default audit retention; streaming format; write-once target; AI reviewer input;
  read-logging scope.
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

- [ ] **[Owner]** Read the design; decide repository, licence and name; the open questions in the design.
- [ ] **[H]** Work created from outside (the Host's open question): an identity allowed to create work items for named profiles.
- [ ] T1 contracts; T2 durable firing log with the kill test; T3 scheduler; T4 Host sink; T5 webhooks and Host events; T6 watches;
  T7 rule management ([steps](design/triggers.md#steps)).

- Not needed yet (owner, 2026-10-22): the Postgres queue, the ratio circuit and the later research items are in
  [optional-todo.md](optional-todo.md), each with when it would be worth doing.
- [ ] **[H]** Optional `CallTimeout` for in-process Host modules, matching isolated ones.
- [ ] **[C]** Later: triggers' scheduler enqueues jobs.
- [ ] **[Owner]** Host work items on `IWorkQueue`: not moved (2026-10-22); work items are records that are read by id, listed and
  claimed with filters, and a queue cannot do that. The reasoning and two alternatives (queue the Host's webhook deliveries and
  external-wait polling now; a "delivery hint" queue when the Host goes multi-node) are in the Host's
  [work design](https://github.com/opentail-net/opentail-host/blob/main/docs/design/work-and-continuations.md).

## 29. External reviews, 2026-10-21 (to-fix documents)

Seven review documents, now in [done/](done/): the Markdown frontmatter finding, the additional bug-fix plan, the second, third
and fourth reviews, the follow-up review and the consolidated remediation plan. All fixed 2026-10-22 with regression tests,
except where noted. Breaking changes are marked **B**; the Host adopts them the next time it takes new packages.

- [ ] **[Owner]** Live-provider integration suites (Graph, Gmail, object storage): need dedicated test accounts and least-privilege
  credentials from the owner. Until then, everything that talks to a provider stays labelled Tested.
