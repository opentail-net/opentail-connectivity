# Plan: Redis/Valkey, Pinecone, MongoDB

**Status:** plan, revised twice on 2026-10-23 after two external reviews (ChatGPT). **R1 is built** (the queue-control contract);
the Redis store, Pinecone and MongoDB are not. Facts marked *to verify* come from documentation or memory; check them before the
step that depends on them.

**What the second review changed** (each point checked against the code before accepting it):

- **Pinecone:** Pinecone applies writes and deletes asynchronously, but `IMemoryBackend.DeleteAsync` promises that a deleted record
  never comes back, and a stale vector carries stale governance metadata that the re-check cannot see through. The plan now keeps
  a journal of recent writes that every search checks (below). The embedding contract (generator, model id, metric, `MinScore`),
  dropped in the first revision, is restored; namespaces per tenant are mandatory; replacing a record with fewer chunks has a
  defined clean-up.
- **Redis:** the registry is pruned of keys that expired on their own; circuit version counters get a lifetime; the wording about
  Lua scripts is made exact; two contradictions about R1 are fixed.
- **MongoDB:** `$facet` and `$regex` leave the first version, and the exact expression and accumulator allow-list is written here
  rather than deferred to M2.

**What the first review changed:**

- **Pinecone:** the first version kept record text out of Pinecone, but `IMemoryBackend.SearchAsync` returns `BackendHit(Record,
  ChunkId, ChunkKind, Text, ...)`, so the backend must produce the chunk's text and its record; there is no chunk-retrieval call on
  any other store. Its governance mapping also covered about half of `VectorDataMemoryBackend.Filter` (13 conditions); it missed
  `AclCaptured` (the fail-closed rule), workspace and cross-workspace, project, repository, trust and observation time. And
  "a namespace per collection" left a search over several collections undefined.
- **Redis:** the clock, renewal and circuit compare-and-set rules must be a contract before code. Checking this found a bug in the
  stores we already ship: **SQLite renewed an expired slot** (it overwrote the expiry while the token was still in the table, and
  `RenewSlotAsync` returned nothing, so the caller could not tell). In-memory drops lapsed slots first, so it never had that bug
  (corrected 2026-10-23; the first revision said both). In-memory cancellations never expired while SQLite's did; and R1's suite
  then found that **SQLite's did not either when read**: old marks were deleted only when the next job was cancelled.
- **MongoDB:** a stage allow-list is not an operator policy, and a final `$limit` bounds neither the work nor the response size.
  Cross-collection stages move out of the first version.
- **Estimates** were optimistic; revised below.

| | What it is for | Repository |
|---|---|---|
| **1. Redis/Valkey** | A shared queue-control store: slots, circuits, rate starts, cancellations, for every worker of a queue | Connectivity |
| **2. Pinecone** | A memory backend (managed vector search) with the memory governance applied before ranking | Connectivity |
| **3. MongoDB** | An agent tool: governed read-only queries over one collection at a time | Host |

Not planned: agents reading Redis directly (caches and session tokens), DynamoDB, Cassandra, Neo4j (on request).

---

## 1. Redis/Valkey as the shared queue-control store

A package `OpenTail.Connectivity.Queues.Redis`, `RedisQueueControl : IQueueControl`, through StackExchange.Redis (MIT), working the
same against Redis and Valkey (BSD). Every **mutation** is one Lua script, so it is atomic on the server. Two logical operations
take several calls: `UpdateCircuitAsync` (read, compute in C#, conditional write, retry) and `KeysAsync` (paged reads).

### The contract (written first, in R1, and tested for every store)

**Time.** Slots and rate windows coordinate workers on different machines, so their expiry is decided by **one clock: the
store's**. The interface changes from absolute times to durations (`TryTakeSlotAsync(..., TimeSpan lease)`,
`RenewSlotAsync(..., TimeSpan lease)`, `TryTakeStartAsync(..., JobRateLimit)` without `now`):

- Redis reads its own clock in the script (`redis.call('TIME')`; scripts replicate their effects, so this is allowed in Redis 5+ and
  Valkey, *to verify* for the versions in CI).
- In-memory and SQLite use their `TimeProvider` (they serve one machine, or one shared file).
- Circuits keep caller-supplied times inside `CircuitRecord` (`OpenUntilMs`, probe expiry): a few seconds of skew only shifts a
  cooldown, and the contract says so (accepted skew: under the cooldown, default seconds).

This is a breaking change to `IQueueControl` and to the RabbitMQ queue that calls it. Nothing outside this repository uses them.

**Slots.**

- `TryTakeSlotAsync`: drop expired slots, then take one when fewer than the limit are live.
- `RenewSlotAsync` returns **bool**: true only when the token's slot is **still live**; an expired slot is never revived, and false
  tells the caller the lease is gone (the RabbitMQ queue treats it as a lost lease).
- `ReleaseSlotAsync`: idempotent.

**Rate starts.** A sliding window on the store's clock; a start is recorded only when the window has room (already the RabbitMQ
admission order since the review of 2026-10-23).

**Circuits: read, compute, compare-and-set, retry.**

- The `change` function may run more than once under contention, so it must be **pure** (the interface's documentation says so).
- Each key has a **version counter kept apart from the value** (`ot:{q}:cv:<key>`, incremented on every write including
  deletion). Deleting a circuit does not reset the counter, so a writer that read version 7 cannot write over a circuit that was
  deleted and recreated at version 9.
- **The counter's lifetime:** it is written with a TTL of one day, refreshed on every write. A compare-and-set lasts seconds (the
  retries are bounded), so no writer can hold a version read a day ago; after a day without writes the counter may go, and a
  recreated circuit starts a new count. The counter never disappears while a circuit exists (both are written by one script, with
  the same TTL refresh).
- **Retries are bounded** (10, with jitter); after that the call throws, and the worker treats it as a queue error (back off).

**Cancellations** expire after a retention period in every store (in-memory too, aligned in R1), default the dead-letter
retention.

**Keys and growth.**

- Keys are `<prefix>{<queue>}:...`; the prefix is configurable (default `ot:`), so applications sharing a Redis do not share
  controls. Queue names are encoded (braces and colons escaped) so a name cannot break the hash tag.
- Every slot and start set gets a TTL of its longest live entry (`PEXPIRE` in the same script), so an abandoned key disappears on
  its own. The registry `<prefix>{<queue>}:keys` is pruned **by the scripts that empty a key**, and also **by `KeysAsync`**, which
  removes members whose slot, start and circuit keys are all gone (a key that expired on its own runs no script). `KeysAsync`
  reads the registry with `SSCAN` in pages, never `KEYS`. The registry itself has the TTL of its longest-lived member, refreshed
  by every write.

**Failure.** Redis unreachable means every control call throws; the worker backs off and leases nothing. It never treats a failed
control call as permission.

**Cluster.** All of a queue's keys share a hash tag, so the scripts are valid on Redis Cluster. CI covers standalone Redis and
Valkey only; Cluster is documented as **designed for, not tested** until a Cluster job exists.

### Testing

- **An `IQueueControl` conformance suite** (new), against in-memory, SQLite and Redis/Valkey: limits under concurrent callers;
  expiry; **an expired slot cannot be renewed** (SQLite failed this before R1; in-memory never did); release is idempotent; the rate window's
  edges; circuit compare-and-set under two concurrent writers, and with a delete and recreate between read and write; bounded
  retries; cancellation expiry; `KeysAsync` after keys empty out.
- **Real time for Redis** (its clock cannot be faked), as the RabbitMQ suite already runs.
- **The RabbitMQ conformance checks with `RedisQueueControl`** as the shared store.
- **Worker test with Redis stopped** mid-run: the worker reports queue errors, backs off, starts nothing, and recovers when Redis
  returns.

### Steps

- [x] **R1** The contract: interface change (durations, `RenewSlotAsync` returns bool), conformance suite, and in-memory and
  SQLite aligned to it (fixes the renewal bug and the cancellation difference). RabbitMQ queue updated. *Built 2026-10-23: 10
  checks (`QueueControlConformance`), both stores pass; SQLite's renewal and cancellation-retention bugs fixed; RabbitMQ treats a
  refused renewal as a lost lease and hands the message back.*
- [ ] **R2** `RedisQueueControl`: scripts, version counters with their TTL, key TTLs, registry pruning (scripts and `KeysAsync`),
  prefix and encoding.
- [ ] **R3** CI against `redis:7.4` and `valkey/valkey:8`: the conformance suite, RabbitMQ with the Redis store, the outage test.
- [ ] **R4** Docs: choosing a control store, the time contract, Cluster status, what a Redis outage does.

Size: R1 a session (it changes three stores and their caller); R2 and R3 a session to a session and a half; R4 small.

---

## 2. Pinecone as a memory backend

### Storage: one store, like the VectorData backend

The backend must return a hit's chunk text and parent record, and nothing else in the memory contract can fetch a chunk. So
**Pinecone holds what `MemoryVectorRow` holds**: per chunk, the vector, the chunk text, the record as JSON and the governance
fields, as the pgvector path does today. One store means no two-store consistency to manage.

- **Size:** Pinecone limits metadata per vector (40 KB, *to verify*). The record JSON is repeated on every chunk, so a large
  record could exceed it; the backend refuses such a record on upsert with a clear error rather than truncating. Measure typical
  sizes in P1.
- **The privacy trade-off** (owner): chunk text and record JSON then live in Pinecone. A variant that keeps them in an authoritative
  store needs a chunk-retrieval contract and a reconciliation design; it is not in this plan and would be its own design.

### Governance: the complete mapping

Every condition of `VectorDataMemoryBackend.Filter` becomes Pinecone metadata and a clause in the filter, applied **before
ranking**; `MemoryGovernance.Deny` still re-checks every hit, but is not relied on. Times are **Unix milliseconds** (not .NET
ticks), converted in one place.

| Condition (`MemoryFilter`) | Metadata | Pinecone clause |
|---|---|---|
| not deleted | `deleted` (bool) | `{deleted: {$eq: false}}` |
| not expired | `expires_ms` (number; "never" is a far-future value, metadata cannot be null) | `{expires_ms: {$gt: now}}` |
| `IncludeSuperseded` false | `superseded` | `{superseded: {$eq: false}}` |
| `IncludeDerived` false | `derived` | `{derived: {$eq: false}}` |
| `IncludeTainted` false | `tainted` | `{tainted: {$eq: false}}` |
| `Collections` (one or more) | `collection` | `{collection: {$in: [...]}}` |
| required claims ⊆ caller's (exact subsets, at most `MaxCallerClaims` = 8) | `claims_key` (sorted, `|`-joined) | `{claims_key: {$in: [every subset of the caller's claims]}}` (up to 256 values) |
| permission scope: unscoped, or one of the caller's | `scope` | `{scope: {$in: ["", ...scopes]}}` |
| **ACL captured, and open or naming a caller principal** (fail closed) | `acl_captured`, `acl_open`, `principals` (list) | `{$and: [{acl_captured: {$eq: true}}, {$or: [{acl_open: {$eq: true}}, {principals: {$in: [...]}}]}]}` |
| `Workspace`: same workspace, or record allows cross-workspace | `workspace`, `cross_workspace` | `{$or: [{workspace: {$eq: w}}, {cross_workspace: {$eq: true}}]}` |
| `ProjectScope` | `project` | `{project: {$eq: p}}` |
| `RepositoryScope` | `repository` | `{repository: {$eq: r}}` |
| `MinTrustLevel` above Low | `trust` (number) | `{trust: {$gte: n}}` |
| `MaxSensitivity` | `sensitivity_rank` | `{sensitivity_rank: {$lte: n}}` |
| `FromUtc` / `ToUtc` (observation time) | `observed_ms` | `{observed_ms: {$gte: from}}`, `{observed_ms: {$lte: to}}` |

`$in` on a list field (`principals`) matches when any element is in the set (Pinecone's documented list semantics, *to verify on the
service and the emulator*). Each row of this table gets a positive and a negative test, and combinations get the conformance suite.

### Collections and tenants

Not a namespace per collection: a search over several collections would have to fan out and merge top-k across namespaces. The
collection is a metadata field filtered with `$in` (the table above), so one query returns a globally correct top-k. Delete by
collection uses the same filter.

**One namespace per tenant, mandatory.** The governance fields have no tenant predicate, so two tenants must never share a
namespace. The backend takes the namespace in its options and refuses to start without one; a deployment serving several tenants
opens one backend per tenant. (Pinecone recommends namespaces for tenant isolation.)

### Freshness: deletes and changes must not come back

Pinecone applies upserts and deletes **asynchronously**: a write is acknowledged before every query reflects it (*to verify* the
window on the service; Pinecone describes seconds). The memory contract does not allow that: a deleted record must never come
back from search. Worse than deletion, a record whose ACL was just narrowed could be returned under its **old** metadata, which the
re-check (`MemoryGovernance.Deny`) cannot see through, because the stale vector carries the stale record JSON.

The strict contract is kept, with a **journal of recent writes**:

- Every upsert, delete and supersede writes `(record id, version, deleted?)` to a journal **before** it calls Pinecone. The
  version is a counter the backend assigns, also stored in each vector's metadata (`version`).
- Every search drops a hit whose record is in the journal as deleted, or whose `version` metadata is older than the journal's.
  Search asks Pinecone for `topK` plus the number of journal entries for the namespace (capped), so dropped hits do not shrink
  the result below `topK` in the usual case.
- Entries leave the journal after a **freshness window** (default 5 minutes, configurable, well above Pinecone's documented
  seconds). The real-service gate (P5) measures the actual lag and checks the default against it.
- **The journal is shared by every process that writes the namespace.** It is an interface (`IMemoryWriteJournal`) with an
  in-process implementation (one writer process), a SQLite one (one machine), and a Redis one once R2 exists (several machines).
  A deployment with several writer processes and an in-process journal is refused at start-up, when the backend can tell (a
  configuration flag), and warned about in the docs otherwise.

This is the complexity the single-store design hoped to avoid; it is the price of Pinecone's write model with a strict deletion
contract. The alternative, documenting a weaker contract (deleted or changed records may appear for a bounded time), is
rejected: an ACL change that lags is a data leak.

### Embeddings and scores

As `VectorDataMemoryBackend` and the SQLite backend:

- **The caller's `IEmbeddingGenerator`** embeds chunks on upsert and the query on search; Pinecone's integrated embedding is not
  used.
- **`PineconeEmbeddingOptions(ModelId, Dimensions, MinScore)`**: the model id is stored on every vector (`model`) and added to every
  filter (`{model: {$eq: id}}`), so vectors from another embedding space are never compared. At start-up the backend reads the
  index's dimension and metric and refuses to start if the dimension differs or the metric is not cosine.
- **Scores:** cosine only in the first version; Pinecone returns the similarity directly. `MinScore` (validated: finite, -1 to 1)
  drops weaker hits, so a question nothing answers returns nothing rather than the nearest unrelated chunks.

### Replacing a record

Chunk ids are `<record id>#<chunk id>`. An upsert of a record:

1. journal the new version;
2. upsert every new chunk with the new `version`;
3. list the record's ids by prefix (serverless indexes support listing by prefix, *to verify*) and delete those not in the new
   set.

Until step 3 is visible, an old chunk can still match; the journal drops it, because its `version` is older. If step 3 fails, the
next upsert or a maintenance sweep (`PineconeMemoryMaintenance.SweepAsync`, deleting chunks whose `version` is older than their
record's newest) cleans it; the journal covers the gap meanwhile. Tests: a record replaced with fewer chunks; a replacement whose
step 3 fails.

### Dependencies and tests

- **SDK:** `Pinecone.Client`, pinned (4.0.2 on 2026-10-23). It targets a newer API than Pinecone Local's (the reviewer cites
  SDK 4 to API 2025-04; the emulator documents 2025-01; *to verify* that the SDK works against the emulator, else pin an older SDK
  for the emulator job or test only against the service).
- **Two separate gates**, never mistaken for each other:
  - **Pinecone Local in CI:** the memory conformance suite, the quality gate, and the mapping tests. Proves the logic.
  - **A real index** (owner's account, run by hand): the same mapping and conformance cases, plus deletion and replacement
    under real lag (search immediately after a delete, an ACL change and a replacement must not return the old state), and a
    measurement of the lag against the freshness window. Only this makes the backend Verified.

The memory conformance suite counts as passed for Pinecone only with the journal in place.

### Steps

- [ ] **P1** Spike: Pinecone Local and the pinned SDK in CI. Acceptance: every row of the mapping table (not only `$or`, `$gt`
  and `$in`), list ids by prefix, describe index (dimension, metric), and metadata sizes of typical records. Whether the emulator
  shows write lag (it may not; the service gate must then cover it). If the emulator lacks an operator, the owner chooses: test it
  against the service only, or change the mapping.
- [ ] **P2** `IMemoryWriteJournal` (in-process and SQLite) and its tests.
- [ ] **P3** `PineconeMemoryBackend`: embedding options and start-up checks, upsert with versions and the replacement protocol,
  the filter, search with the journal and the re-check, delete and supersede, the size refusal, the sweep.
- [ ] **P4** Mapping tests (each row, positive and negative), memory conformance suite and quality gate against Pinecone Local.
- [ ] **P5** Docs and the real-index gate (owner's account), including the measured lag.

Size: P1 half a session; P2 half a session; P3 and P4 two sessions; P5 depends on the account.

---

## 3. MongoDB as an agent tool (Host)

A module package `OpenTail.Modules.MongoDb`, `"Type": "mongodb"`, through the official MongoDB C# driver (Apache 2.0). Read-only,
governed like the Sql module. The login has the built-in **`read`** role on one database: the server is the read-only boundary,
the guard the second line.

### First version: one collection per query

`$lookup`, `$graphLookup` and `$unionWith` are **not** in the first version: they widen what the guard must prove (namespaces in
nested pipelines) and can multiply result sizes. They come later, each with its own tests, if a use case needs joins.

### Tools

| Tool | What it does |
|---|---|
| `<id>.collections` | Collections on the allow-list, each with sampled field names and BSON types. |
| `<id>.find` | `filter`, `projection`, `sort`, `limit` on one allowed collection. |
| `<id>.aggregate` | A pipeline on one allowed collection, within the stage and operator policy. |
| `<id>.count` | `countDocuments` with a filter. |

### The operator policy (an allow-list, not a deny-list)

The guard walks every value of every document the model supplies, recursively, and every key starting with `$` must be on an
allow-list **for the context it appears in**; anything else is refused, so an operator MongoDB adds later is refused until it is
reviewed.

- **Stages** (`aggregate` only): `$match`, `$project`, `$addFields`, `$set`, `$unset`, `$group`, `$sort`, `$limit`, `$skip`,
  `$unwind`, `$count`, `$sortByCount`, `$replaceRoot`, `$replaceWith`, `$sample` (size capped). Not in the first version:
  `$facet` (each branch builds an array inside one document, which a final `$limit` does not bound), `$bucket`, `$bucketAuto`.
- **Query operators** (in `filter`, `$match`): `$eq`, `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$nin`, `$and`, `$or`, `$nor`,
  `$not`, `$exists`, `$type`, `$all`, `$elemMatch`, `$size`, and `$expr` (its contents under the expression list). Not in the
  first version: `$regex` (a length limit is not a regex policy; a restricted subset, such as an anchored literal prefix, can come
  later with its own tests).
- **Expression operators** (in `$expr`, `$project`, `$addFields`/`$set`, `$group` keys, `$replaceRoot`/`$replaceWith`,
  `$sortByCount`), exactly these:
  - arithmetic: `$add`, `$subtract`, `$multiply`, `$divide`, `$mod`, `$abs`, `$ceil`, `$floor`, `$round`, `$trunc`;
  - comparison: `$eq`, `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$cmp`;
  - boolean and conditional: `$and`, `$or`, `$not`, `$cond`, `$ifNull`, `$switch`;
  - string: `$concat`, `$substrCP`, `$strLenCP`, `$toLower`, `$toUpper`, `$trim`;
  - date: `$year`, `$month`, `$dayOfMonth`, `$dayOfWeek`, `$week`, `$hour`, `$minute`, `$dateToString`, `$dateTrunc`, `$dateDiff`;
  - type: `$type`, `$toString`, `$toInt`, `$toLong`, `$toDouble`, `$toDecimal`, `$toDate`, `$toBool`;
  - array (reading, never building): `$size`, `$arrayElemAt`, `$in`, `$first`, `$last`;
  - `$literal`.
- **Accumulators** (in `$group` only): `$sum`, `$avg`, `$min`, `$max`, `$count`, `$first`, `$last`. Not `$push` or `$addToSet`
  (they build arrays as large as the group), nor `$accumulator`.
- Each operator is allowed only in its context: a query operator inside `$project` is refused, an accumulator outside `$group`
  is refused. A test enumerates every operator in the lists against every context.
- **Always refused**, at any depth: `$where`, `$function`, `$accumulator` (server-side JavaScript); `$out`, `$merge` (writes);
  `$lookup`, `$graphLookup`, `$unionWith`, `$facet`, `$regex` (first version); `$currentOp`, `$listSessions`, `$collStats`,
  `$indexStats`, `$planCacheStats`, `$documents`, `$changeStream` (server internals and other sources).
- **Collections:** an allow-list in configuration; `system.*` never.

### Resource bounds (`maxTimeMS` is the last line, not the only one)

| Bound | Default |
|---|---|
| `maxTimeMS`, enforced by the server | `CommandTimeoutSeconds` (15 s) |
| Stages per pipeline | 20 |
| Nesting depth of any supplied document | 32 |
| `$sample` size | `MaxRows` |
| Rows returned | `MaxRows` (200); a `$limit` appended to pipelines and a cursor limit on `find` |
| **Response size**: total bytes of the serialised result | 256 KB; reading stops and the result says it was cut |
| Strings in values | `MaxCellChars` (2,000) |
| Nested arrays and documents in output | depth 8 and 200 elements per array, then summarised |
| `allowDiskUse` | false |

What these do not bound: the work inside `$group` (one group per distinct key) and `$sort` without an index. The server bounds
them (100 MB per stage with `allowDiskUse: false`, then the stage fails) and `maxTimeMS` stops them; the guard cannot. That is
accepted for the first version and stated in the guide.

### Output and governance

- One serialiser for all four tools: relaxed Extended JSON, recursively applying the string, depth, array and byte limits;
  binaries summarised.
- Collection names, sampled field names and values are **untrusted** (taint the caller), with the Sql module's notice.
- `comment: "opentail:<action id>"` on every operation (profiler, `currentOp`, Atlas query insights).
- Tool descriptors: all four are **Read** risk with `ReadsUntrusted`; the suggested policy runs them directly.
- Optional `ReadPreference: "secondaryPreferred"`.
- Credentials as the Sql module (password credential, `mongodb+srv://` for Atlas); traffic does not go through the HTTP guard and
  the manifest says so.

### Testing

- **Guard:** every refused operator at the top level, inside `$expr`, `$elemMatch`, `$not` and `$cond`, and at the depth limit;
  an operator not on the list is refused; stage count and depth limits; the expression list contains nothing refused.
- **Output:** the byte, depth, array and string limits on deeply nested results, for all four tools.
- **Real server in CI:** `mongo:8` with a `read` user and a setup user: the four tools; the read user cannot write; `maxTimeMS`
  stops a slow pipeline (a `$group` over a large generated collection with a short timeout); the response cap on wide documents.
- **Atlas M0** (owner's account) for Verified on Atlas.

### Steps

- [ ] **M1** Skeleton, configuration, credentials, the four tools with descriptors, the shared serialiser and its limits.
- [ ] **M2** The operator policy: walker, context-specific allow-lists, refused set, collection allow-list, stage and depth bounds.
- [ ] **M3** Server limits: `maxTimeMS`, row cap, `comment`, read preference; the response cap.
- [ ] **M4** CI against `mongo:8`; guide (`docs/guides/mongodb.md`).
- [ ] **M5** Atlas M0 run (owner's account), then Verified.
- [ ] **M6** Later, if a use case needs them: `$facet` with per-branch limits; a restricted `$regex`; `$lookup` (then `$unionWith`,
  `$graphLookup` with a depth bound), each with
  namespace tests inside nested pipelines.

Size: M1 to M3 two to three sessions (M2 is most of it); M4 half a session; M5 depends on the account.

---

## Order and what each waits on

1. **R1** (done), then **R2 to R4**. Nothing needed from the owner.
2. **P1**, then P2 to P4 (the journal before the backend). P5 needs a Pinecone account. A Redis journal follows R2.
3. **M1 to M4** need nothing from the owner; M5 needs an Atlas account.

## Decisions (owner, 2026-10-23)

- **Pinecone:** chunk text and record JSON are stored in Pinecone in the first version.
- **MongoDB:** the first version is one collection per query.
- **Valkey** in the examples; Redis and Valkey both in CI.

Still open: accounts for P5 (Pinecone Starter) and M5 (Atlas M0) when the emulator and container work is done.
