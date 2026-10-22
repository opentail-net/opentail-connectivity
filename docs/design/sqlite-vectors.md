# Design: vector search in the SQLite memory backend

**Status:** proposal, 2026-10-23. Nothing is built. Owner: "let's write its own plan".

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

## The idea: exact search after the governance filter, no extension

1. **Storage.** One more table beside the records: `record_vectors(record_key, model_id, dims, vector BLOB)`. Float32,
   little-endian, **normalised at write** so cosine similarity is a dot product. Any dimension; the model id is stored with each
   vector.
2. **Query.** The governance filter that keyword search already uses (deleted, expiry, superseded, derived, taint, collections,
   claims, scopes, ACL, workspace, project, repository, trust, sensitivity, time window) selects the allowed record keys in SQL.
   Only their vectors are read, and similarity is computed in process with `System.Numerics.Tensors.TensorPrimitives` (SIMD).
   Top-k by a bounded heap. Filters before ranking by construction: a record the caller may not see is never scored.
3. **Embeddings come from the caller** (`IEmbeddingGenerator<string, Embedding<float>>`), as in the VectorData backend: at
   upsert for the record's content, at search for the query. Without a generator the backend stays keyword only and says so in
   its capabilities.
4. **Hybrid.** When both are asked for, keyword (FTS5 BM25) and vector results are fused with reciprocal rank fusion (RRF,
   k = 60), which needs no score calibration between the two. Capabilities declare `Keyword | Vector | Hybrid`.
5. **Changing the embedding model.** `EmbeddingModelId` in the capabilities; vectors from another model are not compared with
   the query (different spaces). A re-embed operation rebuilds them in batches (resumable), and until it finishes those records
   are found by keyword only. Mixing models in one ranking never happens.
6. **Consistency.** The vector row lives and dies with its record in the same transaction: upsert replaces it, delete (by id,
   source or collection) removes it, supersede keeps it but the filter excludes it. Taint, labels and policy are on the record, not
   the vector, so a vector can never be found without its record's rules applying.

## Scale, honestly

Exact search reads every allowed vector. Rough cost: 10,000 records of 1,536 dimensions is about 60 MB of vectors and a few
milliseconds of dot products; 100,000 is about 600 MB and becomes noticeable. Target: **local-first, up to tens of thousands of
records**. Beyond that, the answer is Postgres with pgvector through `VectorDataMemoryBackend`, which already passes the suite.
The backend reports its record count against a documented ceiling, and `doctor`-style checks warn past it.

Speed-ups if a project needs them, in order: keep vectors of the allowed set cached per collection; store float16 (half the
size, small recall loss); and only then an index (sqlite-vec once it reaches 1.0, or an HNSW library), which must still filter
before ranking. Recorded in [optional-todo.md](../optional-todo.md).

## Not doing

- No native extension, no alpha dependency, no approximate index in the first version.
- No vectors without their record (the vector table is not a second store with its own rules).
- No silent fallback: a search that asks for vectors on a backend without a generator gets a clear "keyword only" in the result,
  not keyword results dressed as semantic ones.

## Steps

- [ ] **V1** Vector table, normalised float32 storage, write and delete with the record in one transaction; capabilities and
  `EmbeddingModelId`; re-embed operation (batched, resumable).
- [ ] **V2** Vector search: governance filter in SQL, exact similarity with `TensorPrimitives`, bounded top-k.
- [ ] **V3** Hybrid: RRF over FTS5 and vector results; `MemorySearchKinds.Hybrid`.
- [ ] **V4** Conformance: the memory suite's checks that apply to vector and hybrid search (filters before ranking, words cannot
  raise trust, uncaptured ACLs restrict, claims, taint across updates, superseded not current), plus new ones: a vector from
  another model is never compared; a deleted record's vector is gone; re-embed leaves no record unfindable by keyword.
- [ ] **V5** Quality gate: the existing `MemoryQualityGate` with the hashed stand-in embedder (as the pgvector tests use) and,
  where an embedding model is available, a real one; recall@k and MRR against the keyword-only baseline.
- [ ] **V6** Measure and document: search time and memory at 1k, 10k and 50k records; the ceiling in the docs and in a warning.

Size: about a session for V1 to V4, plus half a session for V5 and V6.

## Open questions

- **Default ceiling** for the warning (proposed: 50,000 records with vectors).
- **Hybrid by default** when a generator is present, or only when asked (proposed: hybrid by default; keyword-only on request).
- **Where the generator comes from** in the Host's memory module (frozen with tiers 2 and 3, plan section 20 M9): the same question
  the VectorData backend leaves to the caller.
