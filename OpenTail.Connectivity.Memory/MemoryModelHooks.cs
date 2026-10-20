namespace OpenTail.Connectivity.Memory;

// The only steps that call a model. This repository has no AI models: the Host or opentail implements these, and without them
// the memory stack is fully deterministic.

/// <summary>Rewrites a follow-up question into a standalone query, using the conversation so far.</summary>
public interface IQueryCondenser
{
    Task<string> CondenseAsync(string query, string chatHistory, CancellationToken cancellationToken = default);
}

/// <summary>Re-orders candidates by relevance to the query. Must return a subset of the candidates it was given.</summary>
public interface IReranker
{
    Task<IReadOnlyList<BackendHit>> RerankAsync(string query, IReadOnlyList<BackendHit> candidates, int topK, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a record into atomic facts. Facts it returns are derived: they inherit the record's policy, including its taint, and
/// point back at it.
/// </summary>
public interface IFactExtractor
{
    Task<IReadOnlyList<MemoryFact>> ExtractAsync(MemoryRecord record, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default);
}
