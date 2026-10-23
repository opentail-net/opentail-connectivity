# Design: vector search in the SQLite memory backend

**Status:** proposal, revised 2026-10-23 after an external review (ChatGPT), whose points were checked against the code and
adopted except where noted. Nothing is built. Owner: "let's write its own plan".

## Why

Local-first memory today is keyword only: `SqliteMemoryBackend` uses FTS5 and applies every governance rule in SQL before
ranking. Semantic search exists only through `VectorDataMemoryBackend`, which needs a vector database (verified on Postgres with
pgvector). A laptop, a single-machine Host or a test should get semantic and hybrid recall from the same SQLite file, with the
same guarantees.

## What exists, and why it is not reused

- **The main repository** (`C:\Git\OpenTail`, `OpenTail.Pro/VectorDatabase.cs`) has sqlite-vec `vec0` tables and a KNN search. It
  loads the extension by name but nothing ships it, so it is silently off; its vectors are fixed at 1,536 dimensions; and it ranks
  first and joins records afterwards, with no permission filter before ranking. Never used in anger.
- **Microsoft's SQLite connector** for `Microsoft.Extensions.VectorData` (verified 2026-10-23, docs updated 2026-07-27): preview,
  now `CommunityToolkit.VectorData.SqliteVec`; filters are `EqualTo` only; data properties cannot be lists of strings. Our
  governance filter needs OR, ranges and "list contains" (ACL principals), so `VectorDataMemoryBackend` cannot run on it.
- **sqlite-vec** itself is pre-1.0 (0.1.7-alpha on NuGet).

## Design

### 1. One vector per chunk, per model

`MemoryChunk` lets a record have many chunks, and `VectorDataMemoryBackend` embeds each chunk and returns chunk-level hits
(`BackendHit.ChunkId`). SQLite does the same; one vector per record would behave differently and match long records poorly.

```sql
CREATE TABLE chunk_vectors (
    chunk_id     TEXT NOT NULL,
    record_id    TEXT NOT NULL,
    model_id     TEXT NOT NULL,
    dimensions   INTEGER NOT NULL CHECK (dimensions > 0),
    content_hash TEXT NOT NULL,           -- hash of the exact text sent to the generator
    vector       BLOB NOT NULL,
    PRIMARY KEY (chunk_id, model_id),
    CHECK (length(vector) = dimensions * 4));
CREATE INDEX ix_chunk_vectors_model_record ON chunk_vectors (model_id, record_id);
```

### 2. Vector format and validation

- **Float32, little-endian, always**, written and read with `BinaryPrimitives` and `BitConverter.SingleToInt32Bits`, never the
  machine's byte order.
- **Rejected at write and at query:** wrong dimensions, any non-finite value, a zero (or near-zero) norm; a generator that returns a
  different number of embeddings than it was given texts.
- **L2-normalised** at write and for the query, so cosine similarity is the dot product (`TensorPrimitives.Dot`), clamped to
  [−1, 1] against rounding. A stored BLOB of the wrong length or with non-finite values is treated as corrupt: skipped and reported,
  never scored.
- Packages for `OpenTail.Connectivity.Memory.Sqlite`: `Microsoft.Extensions.AI.Abstractions` (already pinned centrally) and
  `System.Numerics.Tensors` (a .NET 10 pin to add).

### 3. Atomic upsert, with embeddings generated first

Embedding may call a slow local model or a remote service, so it never runs inside a transaction:
1. **Outside the transaction:** validate the chunks; generate all embeddings in one batch call; validate, normalise and encode them;
   compute the content hashes.
2. **One transaction:** replace the record, its metadata, chunks, FTS rows and the vectors for those chunks; vectors of chunks that
   no longer exist are deleted with them.
3. **Commit.** A failed embedding call leaves the previous version of the record intact and searchable, as the VectorData backend
   already guarantees (`VectorDataUpsertDeleteTests`). Deletion by id, source or collection removes the vectors in the same
   transaction as the records.

### 4. Search

- **One governance predicate, shared.** The SQL that keyword search builds today (deleted, expiry, superseded, derived, taint,
  collections, claims, scopes, ACL principals, workspace, project, repository, trust, sensitivity, time window) is extracted into one
  builder used by keyword and vector search alike, so they cannot drift. The final `MemoryGovernance.Deny` check stays as a backstop,
  but a forbidden chunk never enters a vector ranking in the first place.
- **Vector search:** rows of `chunks ⋈ records ⋈ chunk_vectors` for the active model, under that predicate, read one at a time;
  each scored by dot product; the best k kept in a bounded heap. No list of every candidate vector in memory.
- **FTS no longer gates semantic search.** Today an empty FTS query returns nothing; that stays for keyword search only. Vector
  search embeds the query whatever the FTS parser makes of it, and hybrid search with no usable keyword terms runs as vector search.
- **Hybrid:** keyword and vector rankings retrieved independently and fused with the existing `ReciprocalRankFusion.Fuse`
  (k = 60). Raw cosine and BM25 scores are never added together.
- **Capabilities:** without a generator, `Keyword`; with one, `Keyword | Vector | Hybrid` and `EmbeddingModelId` set. Asking
  a keyword-only backend for vector or hybrid search throws `NotSupportedException` (no keyword results dressed as semantic).

### 5. Search modes are honoured (a change to the shared retriever)

`MemoryRetriever` chooses hybrid whenever the backend advertises it and does not look at `MemoryRetrievalRequest.RetrievalMode`, so
the owner's decision ("hybrid by default, keyword-only on request") cannot be honoured by a backend alone. The retriever maps an
explicit mode to a search kind (`Keyword`, `Semantic` to `Vector`, `Hybrid`), checks it against the backend's capabilities and
fails clearly when it is not supported; `Auto` keeps today's choice (hybrid if offered, else keyword and vector fused, else what
there is). This applies to every backend, and is tested on all of them.

### 6. Changing the embedding model, resumably

- **Model identity:** `model_id` names the embedding space exactly: model, revision, dimensions, and any change to how text is
  prepared before embedding. A friendly name alone is not enough.
- **One configured model at a time** (refining the review): queries can only be embedded with the generator in hand, so semantic
  search uses only vectors of that model. Vectors of an older model are never compared with new query vectors; their records are
  found by the keyword half of hybrid search until re-embedded.
- **Re-embedding** (`SqliteMemoryVectorMaintenance`, in the SQLite package, not on `IMemoryBackend`): finds chunks with no vector for
  the current model, or one whose `content_hash` no longer matches; embeds them in batches; and before writing each vector checks
  that the chunk still has the hash that was embedded, discarding the result if the text changed meanwhile (the upsert supplies the
  right one). Restarting skips everything already done.
- **State:** a small table records the current model, whether re-embedding is complete, and how many chunks remain, for diagnostics.
  Opening the database with a different configured model never compares old vectors; it reports the model change and the work left.
- **Cleanup:** vectors of other models are deleted once the current model covers every chunk (or on request).

### 7. Schema migration

The backend refuses any schema version but its own (`SchemaVersion = 1`). Version 2 adds the vector and state tables through an
explicit 1-to-2 migration that keeps records, chunks and the FTS index; a migrated database stays keyword-searchable and starts
without vectors until they are generated.

## Scale, honestly

Exact search reads every allowed vector, and there is one per chunk. Raw vector data at 1,536 dimensions, float32:

| Vectors | Raw size |
|---|---|
| 10,000 | about 61 MB |
| 50,000 | about 307 MB |
| 250,000 | about 1.5 GB |

Not counting chunk text, indexes and the process. 50,000 records of five chunks each are 250,000 vectors. So the warning counts
**vectors and their bytes**, not records. *The owner agreed to a 50,000 ceiling when the plan counted records; at five chunks a
record that is 250,000 vectors (about 1.5 GB), too much for exact search. Proposed instead: warn at 50,000 vectors (about 300 MB
at 1,536 dimensions), and report records, vectors and bytes. To confirm with the owner.* Beyond that, Postgres with pgvector
through `VectorDataMemoryBackend`. Speed-ups if needed, in order: caching per collection, float16, then an index (sqlite-vec at
1.0, or HNSW) that still filters before ranking, all in [optional-todo.md](../optional-todo.md).

## Not doing

- No native extension, no alpha dependency, no approximate index in the first version.
- No vectors without their chunk and record (the vector table is not a second store with its own rules).
- No silent fallback from semantic to keyword.

## Steps

- [ ] **V1 Storage:** per-chunk table, model and content hashes, explicit binary format and validation, packages, atomic upsert
  with embeddings first, deletions; the 1-to-2 migration.
- [ ] **V2 Vector search:** the shared governance predicate (extracted from keyword search), exact dot product, bounded top-k,
  corrupt rows skipped and reported.
- [ ] **V3 Hybrid and modes:** RRF; FTS no longer gates semantic search; explicit modes in `MemoryRetriever` for every backend.
- [ ] **V4 Re-embedding:** state table, batches, restart, stale-content protection, cleanup, diagnostics.
- [ ] **V5 Conformance and quality:** the memory suite's checks for vector and hybrid search, plus the tests listed below; the
  quality gate with the hashed stand-in embedder, and a real model where one is available (never required by the core suite).
- [ ] **V6 Measurement:** time, allocations and memory at 1k, 10k and 50k vectors, with a multi-chunk set; the threshold in
  vectors and bytes, in the docs and in a warning.

Tests to add, beyond the suite: a record survives a failed embedding during replacement; replacing a record replaces its vectors,
and removed chunks and deleted records leave no vector rows; wrong dimensions, zero vectors, non-finite values and malformed
BLOBs are rejected; vector search returns allowed results even when forbidden ones would score higher; keyword mode generates no
embeddings, semantic mode does not depend on FTS parsing; hybrid uses RRF and the requested mode is honoured; re-embedding resumes,
ignores stale vectors for changed chunks, and never compares two models; a version-1 database migrates without losing records or
FTS search.

Size (revised; the first estimate was optimistic): V1 about a session; V2 and V3 about a session; V4 to V6 about a session.
Built in that order, each a testable checkpoint.

## Decisions and open questions

- *Decided (owner, 2026-10-23):* search is **hybrid by default** when a generator is present, keyword-only on request (needs §5).
- *Decided, to re-confirm:* the warning ceiling. Agreed at 50,000 *records*; per-chunk storage makes that up to ~250,000 vectors.
  Proposed: **50,000 vectors** (about 300 MB at 1,536 dimensions).
- **Where the generator comes from** in the Host's memory module (frozen with tiers 2 and 3, plan section 20 M9): the same question
  the VectorData backend leaves to the caller.
