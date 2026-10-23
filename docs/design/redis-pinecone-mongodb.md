# Plan: Redis/Valkey, Pinecone, MongoDB

**Status:** plan, revised 2026-10-23 after an external review (ChatGPT) of the first version. Nothing is built. Facts marked *to
verify* come from documentation or memory; check them before the step that depends on them.

**What the review changed** (each point checked against the code before accepting it):

- **Pinecone:** the first version kept record text out of Pinecone, but `IMemoryBackend.SearchAsync` returns `BackendHit(Record,
  ChunkId, ChunkKind, Text, ...)`, so the backend must produce the chunk's text and its record; there is no chunk-retrieval call on
  any other store. Its governance mapping also covered about half of `VectorDataMemoryBackend.Filter` (13 conditions); it missed
  `AclCaptured` (the fail-closed rule), workspace and cross-workspace, project, repository, trust and observation time. And
  "a namespace per collection" left a search over several collections undefined.
- **Redis:** the clock, renewal and circuit compare-and-set rules must be a contract before code. Checking this found a bug in the
  stores we already ship: **in-memory and SQLite both renew an expired slot** (they overwrite the expiry while the token is still in
  the table, and `RenewSlotAsync` returns nothing, so the caller cannot tell). In-memory cancellations also never expire while
  SQLite's do.
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
same against Redis and Valkey (BSD). Every operation is one Lua script, so it is atomic on the server.

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
- **Retries are bounded** (10, with jitter); after that the call throws, and the worker treats it as a queue error (back off).

**Cancellations** expire after a retention period in every store (in-memory too, aligned in R1), default the dead-letter
retention.

**Keys and growth.**

- Keys are `<prefix>{<queue>}:...`; the prefix is configurable (default `ot:`), so applications sharing a Redis do not share
  controls. Queue names are encoded (braces and colons escaped) so a name cannot break the hash tag.
- Every slot and start set gets a TTL of its longest live entry (`PEXPIRE` in the same script), so an abandoned key disappears on
  its own. The registry `<prefix>{<queue>}:keys` is pruned **by the scripts that empty a key**, not only by `KeysAsync`, and
  `KeysAsync` reads it with `SSCAN` in pages, never `KEYS`.

**Failure.** Redis unreachable means every control call throws; the worker backs off and leases nothing. It never treats a failed
control call as permission.

**Cluster.** All of a queue's keys share a hash tag, so the scripts are valid on Redis Cluster. CI covers standalone Redis and
Valkey only; Cluster is documented as **designed for, not tested** until a Cluster job exists.

### Testing

- **An `IQueueControl` conformance suite** (new), against in-memory, SQLite and Redis/Valkey: limits under concurrent callers;
  expiry; **an expired slot cannot be renewed** (fails today on in-memory and SQLite); release is idempotent; the rate window's
  edges; circuit compare-and-set under two concurrent writers, and with a delete and recreate between read and write; bounded
  retries; cancellation expiry; `KeysAsync` after keys empty out.
- **Real time for Redis** (its clock cannot be faked), as the RabbitMQ suite already runs.
- **The RabbitMQ conformance checks with `RedisQueueControl`** as the shared store.
- **Worker test with Redis stopped** mid-run: the worker reports queue errors, backs off, starts nothing, and recovers when Redis
  returns.

### Steps

- [ ] **R1** The contract: interface change (durations, `RenewSlotAsync` returns bool), conformance suite, and in-memory and
  SQLite aligned to it (fixes the renewal bug and the cancellation difference). RabbitMQ queue updated.
- [ ] **R2** `RedisQueueControl`: scripts, version counters, TTLs, registry pruning, prefix and encoding.
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

### Collections: one namespace, collection as metadata

Not a namespace per collection: a search over several collections would have to fan out and merge top-k across namespaces. The
collection is a metadata field filtered with `$in` (the table above), in **one namespace per deployment** (or per tenant, when a
deployment serves several), so one query returns a globally correct top-k. Delete by collection uses the same filter.

### Dependencies and tests

- **SDK:** `Pinecone.Client`, pinned (4.0.2 on 2026-10-23). It targets a newer API than Pinecone Local's (the reviewer cites
  SDK 4 to API 2025-04; the emulator documents 2025-01; *to verify* that the SDK works against the emulator, else pin an older SDK
  for the emulator job or test only against the service).
- **Two separate gates**, never mistaken for each other:
  - **Pinecone Local in CI:** the memory conformance suite, the quality gate, and the mapping tests. Proves the logic.
  - **A real index** (owner's account, run by hand): the same mapping and conformance cases, plus deletion and freshly written
    data (eventual consistency, *to verify*). Only this makes the backend Verified.

### Steps

- [ ] **P1** Spike: Pinecone Local and the pinned SDK in CI; every operator in the mapping table, including `$in` on a list and
  nested `$and`/`$or`; measure metadata sizes of typical records. If the emulator lacks an operator, the owner chooses: test it
  against the service only, or change the mapping.
- [ ] **P2** `PineconeMemoryBackend`: upsert per chunk with the full metadata, the filter, search with re-check, delete and
  supersede by filter, the size refusal.
- [ ] **P3** Mapping tests (each row, positive and negative), memory conformance suite and quality gate against Pinecone Local.
- [ ] **P4** Docs and the real-index gate (owner's account).

Size: P1 half a session; P2 and P3 one and a half sessions; P4 depends on the account.

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

- **Stages** (`aggregate` only): `$match`, `$project`, `$addFields`/`$set`, `$unset`, `$group`, `$sort`, `$limit`, `$skip`,
  `$unwind`, `$count`, `$bucket`, `$bucketAuto`, `$sortByCount`, `$replaceRoot`/`$replaceWith`, `$sample` (size capped), `$facet`
  (its sub-pipelines under the same policy, no nested `$facet`).
- **Query operators** (in `filter`, `$match`): comparison (`$eq`, `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$nin`), logical
  (`$and`, `$or`, `$nor`, `$not`), element (`$exists`, `$type`), array (`$all`, `$elemMatch`, `$size`), `$regex` with `$options`,
  `$expr` (its contents under the expression list).
- **Expression operators** (in `$expr`, `$project`, `$group`, ...): an explicit list of arithmetic, comparison, boolean, string,
  date, array, conditional, type and accumulator operators, taken from MongoDB's operator reference when M2 is built; a test fails
  if the list contains anything from the refused set.
- **Always refused**, at any depth: `$where`, `$function`, `$accumulator` (server-side JavaScript); `$out`, `$merge` (writes);
  `$lookup`, `$graphLookup`, `$unionWith` (first version); `$currentOp`, `$listSessions`, `$collStats`, `$indexStats`,
  `$planCacheStats`, `$documents`, `$changeStream` (server internals and other sources).
- **Collections:** an allow-list in configuration; `system.*` never.

### Resource bounds (`maxTimeMS` is the last line, not the only one)

| Bound | Default |
|---|---|
| `maxTimeMS`, enforced by the server | `CommandTimeoutSeconds` (15 s) |
| Stages per pipeline, including `$facet` sub-pipelines | 20 |
| Nesting depth of any supplied document | 32 |
| `$sample` size | `MaxRows` |
| Rows returned | `MaxRows` (200); a `$limit` appended to pipelines and a cursor limit on `find` |
| **Response size**: total bytes of the serialised result | 256 KB; reading stops and the result says it was cut |
| Strings in values | `MaxCellChars` (2,000) |
| Nested arrays and documents in output | depth 8 and 200 elements per array, then summarised |
| `allowDiskUse` | false |

`$regex` runs on the server and is bounded by `maxTimeMS`; the guard refuses patterns over 500 characters.

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

- **Guard:** every refused operator at the top level, inside `$expr`, inside `$facet`, inside `$elemMatch`, and at the depth limit;
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
- [ ] **M6** Later, if a use case needs joins: `$lookup` (then `$unionWith`, `$graphLookup` with a depth bound), each with
  namespace tests inside nested pipelines.

Size: M1 to M3 two to three sessions (M2 is most of it); M4 half a session; M5 depends on the account.

---

## Order and what each waits on

1. **R1** first: it fixes a bug in the stores we ship, independent of Redis. Then **R2 to R4**. Nothing needed from the owner.
2. **P1**, then P2 and P3. The owner decides the privacy trade-off before P2 (text and record JSON stored in Pinecone, proposed for
   the first version); P4 needs a Pinecone account.
3. **M1 to M4** need nothing from the owner; M5 needs an Atlas account.

## Open questions (owner)

- **Pinecone:** accept chunk text and record JSON in Pinecone for the first version (proposed), or wait for a design that keeps
  them in our own store?
- **Valkey in the examples** (proposed, for its licence), with Redis and Valkey both in CI.
- **MongoDB joins:** confirm that the first version is one collection per query.
- **Accounts:** a Pinecone Starter project and an Atlas M0 cluster when the emulator and container work is done.
