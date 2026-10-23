# Plan: Redis/Valkey, Pinecone, MongoDB

**Status:** plan, 2026-10-23, at the owner's request. Nothing is built. Facts marked *to verify* are from documentation read on that
day or from memory; check them before the step that depends on them.

Three different kinds of work:

| | What it is for | Repository | Why |
|---|---|---|---|
| **1. Redis/Valkey** | A **shared queue-control store**: concurrency slots, circuits, rate starts and cancellations shared by every worker of a queue | Connectivity | Fills a gap we have now: the control stores are in-memory (one process) and SQLite (one machine or one shared file), so RabbitMQ workers on several machines have nothing good to share. |
| **2. Pinecone** | A **memory backend** (managed vector search for memory and RAG) | Connectivity | A customer on Pinecone keeps their vectors there; the memory governance must still apply before ranking. |
| **3. MongoDB** | An **agent tool**: governed read-only queries over document data | Host | The most common business data of the six NoSQL systems considered; the same shape as the Sql module, with its own guard. |

Not planned: agents reading Redis directly (it mostly holds caches and session tokens), DynamoDB, Cassandra and Neo4j (on request).

---

## 1. Redis/Valkey as the shared queue-control store

### Design

A package `OpenTail.Connectivity.Queues.Redis` with `RedisQueueControl : IQueueControl`, through **StackExchange.Redis** (MIT). It
works the same against **Redis** and **Valkey** (the BSD fork); the CI runs both. Redis's own licence changed twice since 2024
(Redis 8 offers AGPLv3 among its options, *to verify*); that concerns whoever runs the server, not this client package.

Every operation is one **Lua script** (`EVALSHA`), so it is atomic on the server, and every key of a queue shares a **hash tag**,
`ot:{<queue>}:...`, so the scripts also work on Redis Cluster (all of a queue's keys sit in one slot).

| `IQueueControl` member | Redis structure | Script |
|---|---|---|
| `MarkCancelledAsync`, `IsCancelledAsync` | `ot:{q}:cancel:<id>` string with a TTL (the dead-letter retention) | `SET ... EX`, `EXISTS` |
| `TryTakeSlotAsync` | `ot:{q}:slots:<key>` sorted set: member = lease token, score = expiry ms | drop expired (`ZREMRANGEBYSCORE`), then `ZCARD < limit` → `ZADD` |
| `RenewSlotAsync`, `ReleaseSlotAsync` | same | `ZADD XX` / `ZREM` |
| `TryTakeStartAsync` | `ot:{q}:starts:<key>` sorted set of start times (sliding window) | drop older than the window, `ZCARD < count` → `ZADD` |
| `UpdateCircuitAsync(change)` | `ot:{q}:circuit:<key>` hash with a version | The change is a C# function, so it cannot run in Lua: read value and version, apply the function, write back only if the version is unchanged (a compare-and-set script); retry on conflict. |
| `KeysAsync` | `ot:{q}:keys` set of keys that have slots, circuits or starts | `SMEMBERS`, then one script per key; keys with nothing left are removed lazily |

Clock: scripts take `now` from the caller (as the other stores do), not Redis's clock, so tests with a fake clock work. Skew
between workers matters as it does for the SQLite store; the docs say so.

Connections: one `ConnectionMultiplexer` per process, passed in (the application owns it); TLS and passwords through the
connection string. Failures surface as exceptions the worker already treats as queue errors (backoff).

**Optional, later:** a Redis Streams `IWorkQueue` (consumer groups, `XAUTOCLAIM` for expired leases). Only if a project wants
Redis instead of RabbitMQ; the control store is the part that is needed now.

### Testing

- An **`IQueueControl` conformance suite** in `OpenTail.Connectivity.Queues` (there is none yet), run against `InMemoryQueueControl`,
  `SqliteQueueControl` and Redis: slots never exceed their limit across concurrent callers; expired slots free up; a renewed slot
  stays; starts respect the window; a circuit change is atomic under contention (two writers, no lost update); cancellations
  expire; `KeysAsync` reports what is held.
- The RabbitMQ conformance checks, run once more with `RedisQueueControl` as the shared store, so the queue and the store are
  proven together.
- CI: a workflow like `queues-rabbitmq.yml` with **`redis:7.4`** and **`valkey/valkey:8`** containers (Linux runners; no Docker
  locally), run by hand or on changes to the package.

### Steps

- [ ] **R1** `IQueueControl` conformance suite; run it against the in-memory and SQLite stores (may find bugs there first).
- [ ] **R2** `RedisQueueControl`: the scripts above, hash-tagged keys, compare-and-set circuits.
- [ ] **R3** CI against Redis and Valkey; RabbitMQ checks with the Redis store.
- [ ] **R4** Docs: choosing a control store (in-memory, SQLite, Redis), Cluster and TLS notes, what a lost Redis means (workers
  back off; nothing is lost from the queue itself).

Size: R1 half a session; R2 and R3 a session; R4 small.

---

## 2. Pinecone as a memory backend

### Why not through Microsoft's connector

The memory package already has `VectorDataMemoryBackend`, over `Microsoft.Extensions.VectorData`, proven with pgvector. The
Semantic Kernel **Pinecone connector supports only equality filters** (*verified in its documentation, 2026-10-23*). Our
governance filter needs `OR` (scopes, claims subsets), greater-than (expiry) and contains (ACL principals), and it must be applied
**before ranking**, or a search returns the top 10 and then drops the ones the caller may not see. Through that connector the
backend would have to filter afterwards, which breaks the memory design's first rule. So: a small backend of our own.

### Design

A package `OpenTail.Connectivity.Memory.Pinecone` with `PineconeMemoryBackend : IMemoryBackend`, through the official **Pinecone
.NET SDK** (*to verify* the package name and that it supports API 2025-01 or later).

- **One index per deployment, a namespace per collection** (Pinecone namespaces partition an index; a search hits one).
  Collections are then separate by construction, as the governance wants.
- **Records:** one vector per chunk (as SQLite and pgvector do), id `<record id>#<chunk>`; the governance fields as metadata:
  `deleted`, `expires_at` (number), `superseded`, `derived`, `tainted`, `claims_key`, `permission_scope`, `acl_open`,
  `principals` (list of strings), `sensitivity`. No nulls (Pinecone metadata cannot hold null): absent fields are written as
  explicit defaults.
- **The filter** is the same predicate as `VectorDataMemoryBackend.Filter`, translated to Pinecone's filter language:
  `$and`/`$or`, `$eq`, `$gt`, and `$in` on the `principals` list (a list field matches when any element is in the given set).
  Every hit is still checked by `MemoryGovernance.Deny` afterwards, as in the other backends.
- **Record bodies stay out of Pinecone.** Metadata is limited per vector (40 KB, *to verify*), and keeping text in a vector
  service widens where sensitive content lives. The backend stores vectors and governance fields; the record itself stays in the
  record store (SQLite or files), joined by id. This matches how the VectorData backend is meant to sit beside a record store.
- **Embeddings** are made by the caller's `IEmbeddingGenerator`, as for SQLite; Pinecone's integrated (server-side) embedding is
  not used, so the model id rule (vectors under one model id are never compared with another's) still holds.
- **Deletion:** by id prefix per record (Pinecone lists ids by prefix in serverless indexes, *to verify*), so a record's chunks go
  together; the memory conformance suite's delete and supersede checks must pass.

### Testing

- **Pinecone Local** (official emulator in Docker): the `pinecone-index` image runs one index in memory (100,000 records, no
  auth, API 2025-01). Its documentation shows metadata filters, but which operators the emulator supports is *to verify*: the
  first thing to check, since `$in` and `$or` are what the design needs.
- The **memory conformance suite** and the **quality gate** against Pinecone Local in CI (a workflow like `memory-pgvector.yml`).
- A **real index** on Pinecone's free Starter plan (*to verify* its limits) for Verified: owner's account and a CI secret, run by
  hand.

### Steps

- [ ] **P1** Spike: Pinecone Local in CI, write a few vectors with list metadata, query with `$or`, `$gt` and `$in`. If the
  emulator lacks one, decide (owner): test filters against the real service only, or restructure the filter.
- [ ] **P2** `PineconeMemoryBackend`: upsert per chunk, the filter translation, search with re-check, delete and supersede.
- [ ] **P3** Conformance suite and quality gate against Pinecone Local in CI.
- [ ] **P4** Docs (index setup, namespaces, what is stored where) and the real-service run (owner's account).

Size: P1 an hour; P2 and P3 about a session; P4 depends on the account.

---

## 3. MongoDB as an agent tool (Host)

### Design

A Host module package `OpenTail.Modules.MongoDb`, `"Type": "mongodb"`, through the official **MongoDB C# driver** (Apache 2.0).
Read-only, governed like the Sql module: policy, audit, taint, data labels, limits.

**Tools**

| Tool | What it does |
|---|---|
| `<id>.collections` | Collections the module may read, each with a sampled field list (names and BSON types from a few documents), so the model knows the shape. |
| `<id>.find` | `filter`, `projection`, `sort`, `limit` on one collection. |
| `<id>.aggregate` | A pipeline on one collection, from an allow-list of stages. |
| `<id>.count` | `countDocuments` with a filter. |

**The guard** (a document walker, not a parser: queries arrive as JSON, so there is no quoting to misread):

- **Stages allowed:** `$match`, `$project`, `$addFields`/`$set`, `$unset`, `$group`, `$sort`, `$limit`, `$skip`, `$unwind`,
  `$count`, `$facet`, `$bucket`, `$bucketAuto`, `$sortByCount`, `$replaceRoot`/`$replaceWith`, `$sample` (capped), `$lookup` and
  `$graphLookup` **only into collections on the module's allow-list**, `$unionWith` likewise.
- **Refused anywhere in the document:** `$out` and `$merge` (writes), `$where`, `$function` and `$accumulator` (server-side
  JavaScript), `$currentOp`, `$listSessions`, `$collStats`, `$indexStats`, `$planCacheStats` (server internals), and any stage not
  on the allow-list.
- **Collections:** an allow-list in configuration (`Collections`); system collections never.

**Limits**

- `maxTimeMS` from `CommandTimeoutSeconds`: the **server** stops the operation (stronger than a client-side cancel).
- `MaxRows + 1` as the cursor limit (a `$limit` stage appended to pipelines); `allowDiskUse: false`.
- `comment: "opentail:<action id>"` on every operation, visible in the profiler, `currentOp` and Atlas's query insights: the same
  tracing as Snowflake's query tag.
- Values out as relaxed Extended JSON, strings cut at `MaxCellChars`, binaries summarised, and marked untrusted.
- Optional `ReadPreference: "secondaryPreferred"`, so agents read from secondaries and leave the primary to the application.

**Credentials and network:** the connection string without its password, the `password` credential added when connecting (as the
Sql module does), `mongodb+srv://` for Atlas. Database traffic does not go through the Host's HTTP guard; the manifest says so, as
the Sql module's does. The login should be a user with the built-in **`read`** role on one database: the server is the read-only
boundary, the guard the second line.

### Testing

- **Unit:** the guard (every refused stage and operator, nested in `$facet`, `$lookup` pipelines and `$expr`), the allow-lists,
  the Extended JSON output.
- **Real server in CI:** a `mongo:8` container in the `sql-servers` workflow (or one of its own), with a `read` user and a
  writer for setup: find, aggregate, the server-side timeout (`$function` is refused, so a slow query is made with a large
  `$lookup` or a `$sample` over a generated collection), and the read user's inability to write.
- **Atlas:** the free **M0** cluster (free with no time limit, *to verify*) for Verified on Atlas itself: owner's account.

### Steps

- [ ] **M1** Module skeleton, configuration, credentials, the four tools, Extended JSON output.
- [ ] **M2** The guard: stage allow-list, refused operators anywhere, collection allow-list (including inside `$lookup`).
- [ ] **M3** Limits: `maxTimeMS`, row cap, `comment`, read preference.
- [ ] **M4** CI against `mongo:8`; guide (`docs/guides/mongodb.md`): the `read` role, Atlas network access, what the guard refuses.
- [ ] **M5** Atlas M0 run (owner's account), then Verified.

Size: M1 to M3 about 1.5 sessions; M4 half a session; M5 depends on the account.

---

## Order and what each waits on

1. **R1 to R4** need nothing from the owner (containers in CI).
2. **P1** first, because its answer decides P2's shape; then P2 and P3. P4 needs a Pinecone account.
3. **M1 to M4** need nothing from the owner; M5 needs an Atlas account.

## Open questions (owner)

- **Redis or Valkey first** in the docs' examples? (Proposed: both in CI; Valkey in examples, because of its licence.)
- **Pinecone:** keep record text out of Pinecone (proposed), or store it as metadata for simpler deployments?
- **MongoDB:** is `$lookup` across collections wanted at all, or should a first version stay to one collection per query?
- **Accounts:** a Pinecone Starter project and an Atlas M0 cluster, when the stub and container work is done.
