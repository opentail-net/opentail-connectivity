// Moved from opentail's OpenTail.Core.Memory (MemoryEvalContracts.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed record MemoryEvalScenario(
    string Id,
    string Name,
    string Query,
    int TopK,
    IReadOnlyList<string>? ExpectedHitIds = null,
    IReadOnlyList<string>? ExpectedSources = null,
    IReadOnlyList<string>? ExpectedEntityHints = null,
    bool ExpectNoRestrictedLeakage = true,
    bool ExpectNoStaleSupersededHits = true,
    bool ExpectSummaryGrounded = true,
    MemoryRetrievalRequest? RequestOverride = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record MemoryEvalThresholds(
    double MinHitRate = 0.80,
    double MinPrecisionAtK = 0.60,
    double MinRecallAtK = 0.60,
    double MinMrr = 0.50,
    double MinNdcg = 0.65,
    double MinCitationAccuracy = 0.80,
    double MaxStaleHitRate = 0.20,
    double MaxRestrictedLeakageRate = 0.00,
    double MinSummaryGroundingScore = 0.70);

public sealed record MemoryEvalCaseResult(
    string ScenarioId,
    string Name,
    bool Passed,
    double HitRate,
    double PrecisionAtK,
    double RecallAtK,
    double Mrr,
    double Ndcg,
    double CitationAccuracy,
    double StaleHitRate,
    double RestrictedLeakageRate,
    double SummaryGroundingScore,
    IReadOnlyList<string> Notes);

public sealed record MemoryEvalRunResult(
    DateTimeOffset ExecutedAtUtc,
    int ScenarioCount,
    int PassedCount,
    IReadOnlyList<MemoryEvalCaseResult> Cases,
    IReadOnlyDictionary<string, double> AggregateMetrics,
    MemoryEvalThresholds Thresholds,
    bool Passed);

public interface IMemoryEvalRunner
{
    Task<MemoryEvalRunResult> RunAsync(
        IReadOnlyList<MemoryEvalScenario> scenarios,
        MemoryEvalThresholds? thresholds = null,
        CancellationToken cancellationToken = default);
}
