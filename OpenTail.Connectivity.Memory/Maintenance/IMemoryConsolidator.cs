// Moved from opentail's OpenTail.Core.Memory (IMemoryConsolidator.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed record SemanticConsolidationOptions
{
    public double SimilarityThreshold { get; init; } = 0.92;
    public int MaxCandidatePairs { get; init; } = 200;
    public int BatchSize { get; init; } = 50;
}

public sealed record MemoryMergeSuggestion
{
    public required string CanonicalId { get; init; }
    public required string DuplicateId { get; init; }
    public double Similarity { get; init; }
    public string? Rationale { get; init; }
}

public interface IMemoryConsolidator
{
    Task<IReadOnlyList<MemoryMergeSuggestion>> FindDuplicatesAsync(
        IReadOnlyList<MemoryRecord> candidates,
        SemanticConsolidationOptions? options = null,
        CancellationToken ct = default);
}
