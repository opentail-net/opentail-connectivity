# Design: plug-and-play memory

**Status:** M1 to M6 built on 2026-10-20 (see [plan, section 20](../plan.md#20-plug-and-play-memory)); M7 and M8 wait for a
Connectivity release, M9 for the owner. The contract sketches below were written first; the code is in
`OpenTail.Connectivity.Memory` and differs in detail (for example `MemoryHit` also carries its disposition). Written
2026-10-19 from a reading of `OpenTail.Core.Memory`, `OpenTail.Persistence.Sqlite` and `OpenTail.Connectivity.Index` in the
`opentail` repository, and from the outside work listed under [what others do](#what-others-do).

**Where it fits:** memory is input and output, like everything else in this repository: records go in, records come back out by
search or by id. This design moves opentail's shared memory stack here, makes the backend swappable, and lets any agent use it
through a thin Host module. The Host module is new copied data, so it falls under the owner's freeze on tiers 2 and 3
([plan, section 5](../plan.md#5-ask-the-first-customers)); everything before it is groundwork. Builds on
[shared groundwork](shared-groundwork.md) (stable IDs, item metadata, `source.acl.*`) and
[sync and deletions](sync-and-deletions.md).

## Goal

- **Swap the backend by configuration, not code.** SQLite on one machine, Postgres for a team, a hosted vector database or a
  hosted memory service, each behind the same contract.
- **Any agent can use it.** MCP clients through the Host; .NET agents in-process. opentail becomes one consumer among others.
- **Permissions and trust are fields, never words in the text.** A record cannot raise its own trust or widen its own scope by
  what it says.
- **A backend that passes the conformance suite keeps the guarantees.** Compiling against the interface is not enough.
- **No AI models in this repository.** The few steps that call a model are interfaces the Host or opentail implements, as in
  [summary cards](summary-cards.md).

Not a goal: an agent's own working memory (what it learned earlier in this run). That belongs to whoever runs the agent loop:
opentail's `EntityFactMemory`, or the client's own memory (Claude Code's memory files, ChatGPT's memory). See
[three kinds of memory](#three-kinds-of-memory).

## Three kinds of memory

| Kind | Example | Where it belongs |
|---|---|---|
| Working memory | "AuthService has a null-ref on startup", kept across a context reset in one run | The agent runtime. opentail keeps `EntityFactMemory`; MCP clients bring their own. |
| Shared memory | Decisions, incidents, who owns what, what a tool returned last week, notes people wrote | **This design.** Long-lived, many writers and readers, needs permissions, provenance and forgetting. |
| Document index | Full text of chosen collections, searched by meaning | [Tier 3](../roadmap.md#document-search-in-three-tiers). `Connectivity.Index` today; it becomes one collection kind on the same stack ([M8](#steps)). |

## What others do

Our stack is one take. These are the other ideas worth knowing, and what each changes here. Vendor comparisons are mostly
written by vendors; the claims below are the ones the sources agree on, not rankings.

| Approach | The idea | What we take |
|---|---|---|
| **Mem0** | A model extracts facts from conversations, stores them with embeddings, links entities, and updates or merges them on write. | A hosted service of this kind is a [whole-service plug](#plug-points). Its rewriting on write means weaker guarantees (see [limits](#limits)). |
| **Zep / Graphiti** | A temporal knowledge graph: every fact has a time it became true and a time it stopped. | We already have `ValidFromUtc`, `ValidToUtc` and `IsSuperseded` on `MemoryFact`. The conformance suite tests knowledge updates: a superseded fact must not come back as current. |
| **Letta (MemGPT)** | Tiers: core memory always in the context window, recall and archival stores searched on demand; the agent edits its own memory. | Core memory is working memory, so not ours. Our `MemoryPins` table is the shared equivalent: a few records an administrator pins for everyone. Self-editing becomes "the agent proposes, policy decides" through the Host. |
| **LangMem** | Memory tools on the hot path plus background managers that extract and consolidate. | Same split as ours: writes and search inline, `MemoryMaintenanceService` and the consolidator in the background. |
| **Cognee, GraphRAG** | Build one knowledge graph from many sources for many agents. | Our entities and links (`MemoryEntity`, `MemoryLink`) and the neighbour hop in `HybridRetriever` are a small version. A graph database is a backend capability, not a requirement. |
| **MCP reference memory server** | Entities, relations and atomic observations, through tools such as `create_entities`, `add_observations`, `search_nodes`, `open_nodes`. | The de facto tool vocabulary MCP clients have seen. The Host module should feel familiar to agents that know it, while keeping our policy fields. |
| **Claude's memory tool** | Client-side: the model asks for file operations under `/memories` (view, create, replace, delete) and the application carries them out. | Two things. Plain files are the lowest common denominator: a [file backend](#backends) that people can read and edit. And a Host module could implement the memory tool's commands, so Claude uses shared memory through the tool it already knows (an [open question](#open-questions)). |
| **Microsoft Agent Framework context providers** | `AIContextProvider`: memory runs before and after each agent call and adds context; Mem0 and chat-history providers exist. | The in-process way in for .NET agents, next to MCP for everyone else. opentail and the workbench could use it. |
| **`Microsoft.Extensions.VectorData`** | .NET's common contract for vector stores, with connectors for many databases; some support hybrid search and filters applied before the search. | One adapter over it gives many backends at once. It does not cover keyword-only search, deletions by source, links or our policy semantics, so it is a backend, not our contract. |
| **Typed provenance (MemIR, 2026)** | "Provenance-role collapse": flat text memory forgets where a statement came from and whether it was a claim or just something read. Typed atoms separate raw evidence, retrieval cues and supported claims; only supported claims may serve as facts. | Our rule for untrusted content, stated by a paper: an email's text comes back as *evidence from an untrusted source*, never as a fact. Written into the contract as `RecallRole`. |
| **Memory poisoning (MINJA, 2025)** | An ordinary user, with no access to the store, plants records just by talking to the agent; reported injection success above 95% in idealised conditions. Scoring defences raise the cost but do not stop it. | Writes that come from conversations are untrusted by default, and writes to shared scopes go through the Host's policy (queue, review or deny). Trust is set at write time by who wrote it, never by what the text says. |
| **Benchmarks (LongMemEval, LoCoMo)** | LongMemEval tests five abilities: extraction, multi-session reasoning, temporal reasoning, knowledge updates and abstention. Vendors report scores with different judges and subsets. | Use the five abilities as the categories of our own eval scenarios (`MemoryEvalScenarios`), and add two of ours: permission and trust. Do not chase published scores. |

## What we have

In `opentail`, MIT unless marked:

| Piece | Where | Size | What it is |
|---|---|---|---|
| Contracts | `OpenTail.Core.Memory` | 37 files, no dependencies | `MemoryRecord` with a typed `MemoryPolicy` (sensitivity, trust, retention, permission scope, expiry, required claims), `MemoryFact` with validity times, entities, links, ingestion envelopes |
| Store contract | `IMemoryStore` | 3 methods | `SearchAsync`, `StoreAsync(id, content, metadata JSON)`. **Loses the typed policy** (see below). |
| Planner | `MemoryRetrievalPlanner` | 834 lines | Keyword intent detection, subqueries, boosts (freshness, entity overlap, trust, thread), governance filter |
| Fusion | `HybridRetriever` (in `OpenTail.Core`) | 251 lines | Reciprocal rank fusion of vector and keyword results, a neighbour hop, optional model re-ranking through `IChatConnector` |
| Model hooks | `IQueryCondenser`, re-ranking | small | The only model calls. `LlmQueryCondenser` is in `OpenTail.Core`. |
| Entity extraction | `DefaultMemoryEntityExtractor` | 344 lines | Regular expressions: actors, `owner/repo`, `@mentions`, tool and workflow words |
| Chunkers | `HierarchicalChunker`, `SentenceWindowChunker` | | |
| Normalisers | GitHub, Slack, user note, tool execution, project facts, workflow run, crew handoff, diagnostics | | Turn captured events into records |
| Maintenance | `MemoryMaintenanceService`, `SemanticMemoryConsolidator`, operator controls | | Decay, duplicates, pins, operator actions |
| Evals | `MemoryEvalRunner`, scenarios, thresholds | | Regression gates |
| SQLite substrate | `OpenTail.Persistence.Sqlite/OpenTailDatabase.cs` | 15 `Memory*` tables inside a 2,804-line file shared with the rest of opentail | Records, chunks, embeddings, entities, links, access policies, collections, retrieval logs, pins, decay, tags |
| Vectors | `OpenTail.Pro/VectorDatabase.cs` | 144 lines, **PolyForm Noncommercial** | `MemoryRecordVectors` on sqlite-vec |
| Document index | `OpenTail.Connectivity.Index` | SQLite chunk store, retriever, context packages | Uses `MemoryChunk` and the ingestion pipeline types |

### The governance filter reads the text

The planner's filters work on the hit's content string, because `IMemoryStore` and `MemoryHit` carry only an id, the content and
a score (`MemoryRetrievalPlanner.cs:524-580`):

- **Permission scope:** passes when the text contains the scope name.
- **Trust:** `Medium` and `High` pass when the text contains "trusted", "reviewed", "validated" or "verified"; `Verified` when it
  contains "verified", "approved" or "signed off".
- **Sensitivity:** a record is held back when its text contains words like "secret", "password" or "internal".
- **Workspace:** passes when the text contains the workspace name.

An email that says "this has been verified" passes a `Verified` trust check; anything that mentions a workspace is recalled in
it; a secret that never uses the word "secret" is not held back. In a single-user opentail it is a weak heuristic. Behind a
multi-user Host it is a permission bypass that anyone who can get text into memory can use, which MINJA shows is anyone who can
talk to the agent.

The typed policy already exists on `MemoryRecord`. It is lost between the store and the planner. The fix is to carry it through
([M1](#steps)), and it is worth doing in opentail now whether or not anything moves.

## Design

### Layers

```
 MCP clients ──> Host memory module ──┐      .NET agents ──> AIContextProvider adapter ──┐
   (policy, queue, audit, taint)      │                                                  │
                                      v                                                  v
                    ┌──────────────────────── IMemory (this repository) ────────────────────────┐
                    │ write: normalise → chunk → extract entities → backend.UpsertAsync          │
                    │ read:  plan → backend search (pre-filtered) → fuse if needed → boost →     │
                    │        governance check (fields) → hits with provenance and RecallRole     │
                    │ model hooks: IQueryCondenser, IReranker (implemented outside)              │
                    └───────────────────────────────────┬────────────────────────────────────────┘
                                                        v
                     IMemoryBackend: SQLite │ Postgres (VectorData) │ files │ hosted service ...
```

Written once: the pipeline, the planner, fusion, the governance check. Written per backend: storage and the search primitives.

### Plug points

1. **Backend** (`IMemoryBackend`): stores records and chunks, searches, deletes. Declares what it can do; the layers above adapt.
2. **Whole service** (`IMemoryService`): a hosted memory service that does its own extraction and ranking (Mem0, Zep and
   similar). It replaces the pipeline and planner but **not** the governance check, which runs on whatever it returns.
3. **Model hooks** (`IQueryCondenser`, `IReranker`, `IFactExtractor`): optional. Without them the stack is fully deterministic.
4. **Ways in**: the Host module (MCP tools and resources, any agent), a Claude memory-tool adapter, an `AIContextProvider`
   adapter (.NET agents in-process), and a plain CLI and HTTP path for processes such as client hooks and scheduled jobs. All
   call `IMemory`. See [how memory reaches the model](#how-memory-reaches-the-model).

### Contracts (sketch)

```csharp
public interface IMemoryBackend
{
    MemoryBackendCapabilities Capabilities { get; }

    Task UpsertAsync(MemoryRecord record, IReadOnlyList<MemoryChunk> chunks, CancellationToken ct = default);
    Task<MemoryRecord?> GetAsync(string recordId, CancellationToken ct = default);
    Task<int> DeleteAsync(MemoryDeletion deletion, CancellationToken ct = default);   // by id, by source, by collection

    /// Filters are applied inside the search, before top-k. A backend that cannot do that for a filter must say so in
    /// Capabilities; IMemory then refuses the query rather than filtering afterwards (see "Filter before ranking").
    Task<IReadOnlyList<BackendHit>> SearchAsync(MemorySearch search, CancellationToken ct = default);
}

[Flags]
public enum MemorySearchKinds { Keyword = 1, Vector = 2, Hybrid = 4, Graph = 8 }

public sealed record MemoryBackendCapabilities(
    MemorySearchKinds Search,
    bool FiltersBeforeRanking,       // required for any deployment with more than one principal
    bool TemporalValidity,           // honours ValidFrom/ValidTo/IsSuperseded
    bool DeleteBySource,
    string? EmbeddingModelId);       // null when the backend embeds itself or has no vectors

public sealed record MemorySearch(
    string Query,
    MemorySearchKinds Kind,
    int TopK,
    MemoryFilter Filter);            // typed: principals, collections, max sensitivity, min trust, time range

/// What a hit is allowed to be used as. From MemIR's provenance typing, and our taint rule.
public enum RecallRole { Fact, Evidence, UntrustedEvidence }

public sealed record MemoryHit(
    MemoryRecord Record,             // carries Source and Policy; nothing is inferred from Content
    string ChunkId,
    string Text,
    double Score,
    RecallRole Role,
    IReadOnlyList<string> Reasons);  // the planner's explanation, kept from today's MemoryPlannerHit
```

`MemoryRecord`, `MemoryPolicy`, `MemorySource`, `MemoryFact`, `MemoryEntity` and `MemoryLink` move as they are, with two
additions: `MemorySource` gains the stable id and `source.acl.*` principals from [shared groundwork](shared-groundwork.md), and
`MemoryPolicy` gains `Tainted` (set when the content came from a source the Host marks untrusted). `Role` is `UntrustedEvidence`
whenever `Tainted` is set, whatever the trust level.

### Who writes

| Writer | Example | Default trust | Default path |
|---|---|---|---|
| A connector (ingest) | GitHub issues, notes in a folder | From the source's configuration | Direct; the collection is approved by an administrator, as tier 3 |
| The Host's audit stream | A tool call and its result | `Low`, `Tainted` when the result was | Automatic; no agent cooperation needed |
| An agent ("remember this") | `memory.remember` | `Low`, `Tainted` when the conversation read untrusted content | Through Host policy: private scope runs, shared scopes queue for review by default |
| A person | A note, a correction, a pin | `High` | Direct, audited |

Trust comes from **who wrote it and through which path**, never from what it says.

**Processes are writers too.** The write and search paths are open to processes, not only to agents over MCP: a client hook,
a scheduled job, an import. Each runs under its own identity (an API key with a profile) and the same policy. A summary a hook
extracts from a transcript is marked `IsDerived`, carries the taint of whatever the session read, and follows the shared-write
review rules. The hooks themselves are out of scope; [memory hooks](memory-hooks.md) records how they were checked.

## How memory reaches the model

Written for the models to come, not for today's tool sets. What is likely to last: models are trained hardest on general
building blocks (files, editing, search, a shell); they manage more of their own context and will write memory more often;
they are trained to respect labelled provenance; clients load tools on demand, so fewer, general tools win. Nobody knows the
exact shape in two years, so the tool shape is the cheapest part to change.

**The contract is a document model.** Every memory is a document with a stable address (`memory://<scope>/<id>`) that reads
as Markdown with a header: source, writer, trust, taint, validity times, `RecallRole`. All meaning (policy, review, taint, role,
ranking) stays in the server; the shapes below are adapters and can be added or swapped.

**The deep path: four file-shaped tools.**

| Tool | Does | Policy |
|---|---|---|
| `memory.search` | Query in; addresses, snippets, provenance and role out | Read; filtered before ranking |
| `memory.read` | One document by address | Read |
| `memory.write` | Create a document, or edit one by replacing text | Private scope runs; shared scope queues (answer 3). Edits to one document are batched into one review per window. |
| `memory.forget` | Ask for a document to be deleted | Reviewed |

Descriptions name the triggers: search "before answering questions about past decisions, owners, customers or earlier work";
write "when the user corrects you, states a preference, or a decision is made". The Host describes the memory as **shared team
memory** so it is not confused with a client's own memory.

**Pull alone is weak.** A model reaches for a search tool when the question is plainly about the past, rarely in the middle of
unrelated work, and seldom writes unprompted (seen first-hand on 2026-10-19: an injected memory index was used all session; a
server instruction to call a tool at session start was skipped). So the paths that do not depend on the model come first:

1. **Capture without the agent**: the Host's audit stream, and client hooks at compaction and session end.
2. **A pushed index**: a short summary of what the scope holds, at session start (server instructions, an MCP resource, or a
   `SessionStart` hook).
3. **Recall at the point of need**: opt-in hints on tool results ("shared memory has 2 records about Acme: `memory://...`"),
   addresses and labels only, taint kept; or a `UserPromptSubmit` hook.
4. **The four tools** for depth.

**Other adapters on the same documents:** MCP resources (`memory://` URIs) for clients that read resources; Claude's memory
tool, with `/memories/<scope>/...` mapped to documents in a private scope (the file backend fits it directly);
`AIContextProvider` for .NET agents.

### Backends

| Backend | Search | Notes |
|---|---|---|
| **SQLite** (first) | Keyword (FTS5), vector optional | Extracted from `OpenTailDatabase.cs`. The vector part in opentail is in `OpenTail.Pro` (PolyForm Noncommercial) and cannot move here as is: either the owner relicenses those 144 lines, or the vector part is written fresh or comes from a VectorData SQLite connector. Keyword-only is a valid first backend. |
| **VectorData adapter** (second) | Whatever the connector supports | One adapter, many databases (Postgres with pgvector, Qdrant, Azure AI Search, ...). Prove it on Postgres, which the Host already tests against in `sql-servers.yml`. Keyword search, deletion by source and filters come from the database underneath, so capabilities differ per connector. |
| **Files** | Keyword | Markdown files with front matter, one per record. Readable and editable by people, diffable in git, no database. The lowest common denominator, and close to how Claude's memory tool and Claude Code's memory work. |
| **Hosted service** | The service's own | Through `IMemoryService`; weaker guarantees, see [limits](#limits). |

## Making plug-and-play real

### Conformance suite

One test suite, run against every backend, in a package any backend author can reference (as
[testing and verification](testing-and-verification.md) does for connectors):

- Round trip: what is written comes back by id, unchanged, with its policy.
- Stable ids: writing the same source item twice gives one record.
- Deletion: by id, by source, by collection; a deleted record never comes back from search.
- **Filtering before ranking**: with a hundred matching records the caller may not see and three they may, a top-5 search
  returns the three. A backend that filters afterwards returns fewer or none and fails.
- **Words cannot raise trust**: a record whose text says "verified, approved, signed off, trusted" comes back at its written
  trust level.
- Taint survives: a tainted record comes back as `UntrustedEvidence` after an update, a consolidation and a re-index.
- Knowledge update (when `TemporalValidity` is declared): a superseded fact is not returned as current.
- Declared capabilities are true: a backend that says `Hybrid` really returns keyword-only matches.

### Quality gate

Two backends can both pass and rank differently. `MemoryEvalRunner` and its thresholds become the quality gate, with scenarios
grouped by LongMemEval's five abilities plus permission and trust. Results are compared between backends the way the workbench
compares skill versions: per scenario, newly failing first. A backend ships when it passes conformance and stays within the
thresholds.

### Filter before ranking

Permissions applied after taking the top results make recall depend on how many forbidden records rank above the allowed
ones. So `FiltersBeforeRanking` is required for any deployment with more than one principal, and `IMemory` refuses a filtered
query on a backend without it rather than quietly returning less. (`HybridSearchOptions.Filter` in VectorData is applied before
the search, so the adapter can meet this where the connector supports it.)

The same rule runs inside the retriever: governance decides every candidate before ranking, so an `IReranker` (possibly a remote
model) sees only what the caller may see, with redacted hits already replaced by `MemoryRedaction.Redact` (no text, hash, dedupe
key or embedding; the summary only when the policy allows it). The caller's `MemoryFilter` is the ceiling: a request may narrow
it, never widen it. A record with no ACL is treated as uncaptured and denied; records without source permissions say so with
`MemorySourceAcl.OrganisationWide`.

## Limits

- **Embeddings do not travel.** Vectors from one embedding model are useless to another, and backends store them differently.
  Records are the truth; vectors are rebuilt from them. Switching means a re-index command, not a migration, and its cost grows
  with the store.
- **Hosted services rewrite.** Services that merge and summarise on write do not keep our ids, provenance or validity times
  intact. They are supported as `IMemoryService` with documented weaker guarantees: the governance check still applies, but
  "deleted means gone" and "taint survives" depend on the service, and the conformance suite will show which fail.
- **Recall quality is not portable.** A backend switch can change what comes back. The quality gate measures it; it cannot
  promise it.
- **Poisoning is reduced, not prevented.** Trust by writer, taint, review of shared writes and `RecallRole` limit what a planted
  record can do. A record a reviewer approved can still be wrong.
- **Slack.** opentail has a `SlackMemoryIngestionNormalizer`. The [roadmap](../roadmap.md#two-ways-to-use-business-data) found
  that Slack's 2025 terms rule out storing or indexing Slack content. It does not move, and opentail should check its own use.

## Steps

M1 to M8 are groundwork; M9 waits on the owner. Ranges are rough; the drivers are named.

| | Step | Repo | Size | Driven by |
|---|---|---|---|---|
| M1 | **Fix the governance filter**: carry `MemoryRecord`/`MemoryPolicy` through `IMemoryStore` and `MemoryHit`; filters read fields; tests that words cannot raise trust | opentail | 1-3 days | How many callers use `MemoryHit` and the JSON metadata |
| M2 | **Contracts** here as `OpenTail.Connectivity.Memory`: records, policy (+ `Tainted`), `IMemoryBackend`, capabilities, `MemoryHit` with `RecallRole`, model hook interfaces | connectivity | 1-2 days | Naming; tying `MemorySource` to stable ids and `source.acl.*` |
| M3 | **Move the stack**: pipeline, planner, fusion, chunkers, entity extractor, consolidator, maintenance, evals, generic normalisers (GitHub, note, tool execution, project facts). Not Slack; not workflow, crew or diagnostics (opentail's own) | connectivity | 2-4 days | `HybridRetriever` and `LlmQueryCondenser` live in `OpenTail.Core` and use `IChatConnector`; replace with the hook interfaces |
| M4 | **SQLite backend**, keyword first, from the 15 tables in `OpenTailDatabase.cs` | connectivity | 3-6 days | Untangling a shared 2,804-line file; whether to keep opentail's schema for migration or start clean |
| M5 | **Conformance suite and quality gate**, SQLite as the first backend to pass | connectivity | 2-4 days | How much of `MemoryEvalScenarios` is reusable |
| M6 | **Second backend**: VectorData adapter, proven on Postgres with pgvector; files backend if cheap | connectivity | 3-5 days | Connector maturity; CI time for a Postgres container (Actions minutes are scarce) |
| M7 | **opentail consumes the package**; `Core.Memory` becomes a shim or goes; its own normalisers stay as plug-ins | opentail | 2-5 days | 11 projects reference `OpenTail.Core.Memory` today |
| M8 | **`Connectivity.Index` onto the stack** as a document collection; tier 3 then has a home | connectivity + opentail | 2-4 days | Index's own chunk store vs the backend's |
| M9 | **Host `memory` module**: the four tools, `memory://` resources, the pushed index and recall hints, write policy with batched review, taint, audit, the CLI/HTTP path for processes; `AIContextProvider` and Claude memory-tool adapters | host | 1-3 weeks | Review batching; the recall-hint cost. **Frozen until the owner unfreezes copied data.** |

M1 stands alone and is useful today. M2 to M6 can land without changing opentail. M7 is where opentail starts to
depend on this repository for memory.

## Open questions

For the owner:

1. **Scope of the freeze.** M1 to M8 are treated as groundwork, like section 6. *Owner, 2026-10-19: agreed.*
2. **Licence of the vector code.** Relicense `OpenTail.Pro/VectorDatabase.cs` to MIT, or write the vector part fresh? *Owner,
   2026-10-19: MIT is fine; the vector code moves with the SQLite backend.*
3. **Shared writes.** Review every agent write to a shared scope by default, or only above a sensitivity level? *Decided
   2026-10-19 (the owner delegated it): review by default, and the axis is taint, not sensitivity.* Poisoning works through
   harmless-looking records ("invoices from Acme go to account X" is not sensitive), so a sensitivity threshold lets the attack
   through. Rules: an agent write to a private scope runs; to a shared scope it queues for review; a **tainted** write to a
   shared scope always queues and cannot be relaxed. An untainted shared write can be relaxed per scope with the Host's existing
   [undo window](https://github.com/opentail-net/opentail-host/blob/main/docs/design/undo-window.md) rules and trust ramp
   (recallable only after the window closes).
4. **Host tool surface.** Mirror the MCP reference server's names, implement Claude's memory-tool commands, or both on one
   store? *Decided 2026-10-19 with the owner: neither as the contract. Design for how models will work, not for today's tool
   sets.* The contract is the [document model](#how-memory-reaches-the-model); the tools, MCP resources, Claude's memory tool
   and `AIContextProvider` are adapters on it.

Technical, answered while building:

5. Package name: `OpenTail.Connectivity.Memory`, or a separate `OpenTail.Memory` family in this repository? *Built as
   `OpenTail.Connectivity.Memory`, with backends as `.Memory.Sqlite` and `.Memory.VectorData`.*
6. Does the files backend earn its place before a customer asks for it? *Built: it was cheap, needs no dependency, and is the
   shape Claude's memory tool and hooks use. It passes conformance and the gate.*
7. Keep opentail's SQLite schema so existing local databases migrate, or start clean and re-ingest? *Clean: the record shape
   changed (ACL, taint, writer) and records are the truth, so moving existing data is a re-ingest.*

## What building it found

- **Two more leaks in opentail's planner** besides the text-matched filters: diagnostics for a denied hit carried the first
  180 characters of the denied record, and a hit marked `Redacted` still carried its full text. Both fixed (the second by
  design in `MemoryRetriever`, which withholds the text).
- **An outside review (2026-10-20) found three more** in the retriever as first built here: a request scope was added to the
  caller's scopes, the reranker received sensitive hits before redaction, and a redacted hit's `Record` still held the text; a
  record with no ACL also skipped the ACL check. All fixed with regression tests ([review](../review-2026-10-20.md)).
- **The planner was never constructed in production code.** The bypass was latent; part of what looked finished was
  scaffolding.
- **About 4,000 of 5,800 lines moved unchanged** (record model, ingestion, normalisers, chunkers, entities, maintenance) with
  their tests. Governance, the retriever, the store contract, the eval metrics and the SQLite schema were replaced.
- **The old eval metrics read text** ("restricted leakage" meant the text contained "secret"). The quality gate measures
  from typed fields instead.
- **Required claims do not map onto common vector-store filters** (a subset test). The VectorData adapter matches them exactly
  by listing every subset of the caller's claims, up to eight, and refuses more rather than approximating.
- **Abstention with vectors depends on a minimum score**, and the right value depends on the embedding model. It is a setting
  of the VectorData backend, not a constant.
- **The conformance suite can fail**: a backend that filters after top-k and checks trust by words fails both checks aimed at
  it, and a backend that ignores the caller fails the gate's permission scenarios.

## Sources

- [Memory poisoning attack and defense on memory-based LLM agents (2026)](https://arxiv.org/pdf/2601.05504)
- [MINJA: a practical memory injection attack against LLM agents (2025)](https://arxiv.org/abs/2503.03704v4)
- [MemIR: mitigating provenance-role collapse via typed memory representation (2026)](https://arxiv.org/abs/2605.25869)
- [LongMemEval (ICLR 2025)](https://arxiv.org/abs/2410.10813v2)
- [Claude memory tool](https://platform.claude.com/docs/en/agents-and-tools/tool-use/memory-tool)
- [MCP knowledge graph memory server (mirror of the reference README)](https://glama.ai/mcp/servers/@modelcontextprotocol/fetch/blob/04cce79b4c2bbaa313095e9c2a466381f5fd1e64/src/memory/README.md)
- [Microsoft Agent Framework integrations (context providers)](https://learn.microsoft.com/agent-framework/integrations)
- [.NET vector search with Microsoft.Extensions.VectorData](https://learn.microsoft.com/dotnet/ai/vector-stores/vector-search)
- [`HybridSearchOptions` source](https://source.dot.net/Microsoft.Extensions.VectorData.Abstractions/HybridSearchOptions.cs.html)
- Framework comparisons (vendor-written, read with care): [Atlan](https://atlan.com/know/best-ai-agent-memory-frameworks-2026/),
  [Vectorize](https://vectorize.io/articles/best-ai-agent-memory-systems),
  [Graphlit](https://www.graphlit.com/blog/survey-of-ai-agent-memory-frameworks),
  [Cognee](https://www.cognee.ai/open-source-memory-frameworks-llm-agents)

Not verified: the current release status of the Agent Framework memory providers (sources disagree), which VectorData
connectors support hybrid search and pre-filtering today, and whether a VectorData SQLite connector with sqlite-vec is released.
