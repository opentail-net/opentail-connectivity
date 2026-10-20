namespace OpenTail.Connectivity.Memory;

/// <summary>What a scenario tests. The first five are LongMemEval's abilities; the last two are ours.</summary>
public enum MemoryAbility
{
    Extraction = 0,
    MultiSession,
    Temporal,
    KnowledgeUpdate,
    Abstention,
    Permission,
    Trust,
}

/// <param name="ExpectedRecordIds">Records a good answer contains. Empty for an abstention scenario: nothing should come back.</param>
/// <param name="ForbiddenRecordIds">Records that must never come back for this caller, whatever the ranking.</param>
public sealed record MemoryQualityScenario(
    string Id,
    MemoryAbility Ability,
    MemoryRetrievalRequest Request,
    IReadOnlyList<string> ExpectedRecordIds,
    MemoryFilter? Access = null,
    IReadOnlyList<string>? ForbiddenRecordIds = null);

public sealed record MemoryQualityThresholds(double MinRecallAtK = 0.75, double MinMrr = 0.5);

/// <param name="Leaks">Hits the caller may not see (named forbidden, or denied by governance under the scenario's access).
/// Any leak fails the gate.</param>
/// <param name="UntrustedAsFact">Tainted hits recalled with a role other than untrusted evidence. Any fails the gate.</param>
/// <param name="SupersededAsCurrent">Superseded hits returned when superseded records were not asked for. Any fails the gate.</param>
public sealed record MemoryQualityCase(
    string ScenarioId,
    MemoryAbility Ability,
    bool Passed,
    double RecallAtK,
    double Mrr,
    int Leaks,
    int UntrustedAsFact,
    int SupersededAsCurrent,
    IReadOnlyList<string> ReturnedIds,
    IReadOnlyList<string> Notes);

public sealed record MemoryQualityReport(
    string Backend,
    IReadOnlyList<MemoryQualityCase> Cases,
    IReadOnlyDictionary<MemoryAbility, double> RecallByAbility,
    bool Passed);

/// <summary>A scenario that passed in the baseline and fails in the candidate, or the reverse.</summary>
public sealed record MemoryQualityChange(string ScenarioId, MemoryAbility Ability, bool NowPasses, double RecallBefore, double RecallAfter);

/// <summary>
/// Whether a backend is good enough, not only correct. Loads a corpus into an empty backend, runs the scenarios through
/// <see cref="MemoryRetriever"/>, and measures from typed fields only: recall and MRR against expected records, and three
/// counts that must be zero (leaks, untrusted recalled as more than evidence, superseded recalled as current).
/// Two backends that both pass conformance can still rank differently; <see cref="Compare"/> shows what changed.
/// </summary>
public static class MemoryQualityGate
{
    public static async Task<MemoryQualityReport> RunAsync(
        string backendName,
        IMemoryBackend emptyBackend,
        IReadOnlyList<(MemoryRecord Record, IReadOnlyList<MemoryChunk> Chunks)> corpus,
        IReadOnlyList<MemoryQualityScenario> scenarios,
        MemoryQualityThresholds? thresholds = null,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emptyBackend);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(scenarios);
        var limits = thresholds ?? new MemoryQualityThresholds();
        var clock = time ?? TimeProvider.System;

        foreach (var (record, chunks) in corpus)
            await emptyBackend.UpsertAsync(record, chunks, cancellationToken).ConfigureAwait(false);

        var retriever = new MemoryRetriever(emptyBackend, time: clock);
        var cases = new List<MemoryQualityCase>();
        foreach (var scenario in scenarios)
        {
            var result = await retriever.RetrieveAsync(scenario.Request, scenario.Access, cancellationToken).ConfigureAwait(false);
            cases.Add(Score(scenario, result, clock.GetUtcNow(), limits));
        }

        var byAbility = cases.GroupBy(static c => c.Ability).ToDictionary(static g => g.Key, static g => g.Average(static c => c.RecallAtK));
        return new MemoryQualityReport(backendName, cases, byAbility, cases.All(static c => c.Passed));
    }

    /// <summary>Scenarios whose outcome changed between two reports, newly failing first.</summary>
    public static IReadOnlyList<MemoryQualityChange> Compare(MemoryQualityReport baseline, MemoryQualityReport candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        var before = baseline.Cases.ToDictionary(static c => c.ScenarioId, StringComparer.Ordinal);
        return [.. candidate.Cases
            .Where(c => before.TryGetValue(c.ScenarioId, out var b) && b.Passed != c.Passed)
            .Select(c => new MemoryQualityChange(c.ScenarioId, c.Ability, c.Passed, before[c.ScenarioId].RecallAtK, c.RecallAtK))
            .OrderBy(static ch => ch.NowPasses)
            .ThenBy(static ch => ch.ScenarioId, StringComparer.Ordinal)];
    }

    private static MemoryQualityCase Score(MemoryQualityScenario scenario, MemoryRecallResult result, DateTimeOffset now, MemoryQualityThresholds limits)
    {
        var ids = result.Hits.Select(static h => h.Record.Id).Distinct(StringComparer.Ordinal).ToList();
        var notes = new List<string>();
        var forbidden = scenario.ForbiddenRecordIds ?? [];
        var access = scenario.Access ?? new MemoryFilter();

        var leaks = result.Hits.Count(h => forbidden.Contains(h.Record.Id, StringComparer.Ordinal)
                                           || MemoryGovernance.Deny(h.Record, access with { MaxSensitivity = null }, now) is not null);
        var untrustedAsFact = result.Hits.Count(static h => h.Record.Policy.Tainted && h.Role != RecallRole.UntrustedEvidence);
        var supersededAsCurrent = scenario.Request.IncludeSuperseded ? 0 : result.Hits.Count(static h => h.Record.IsSuperseded);

        double recall, mrr;
        if (scenario.ExpectedRecordIds.Count == 0)
        {
            recall = ids.Count == 0 ? 1 : 0;
            mrr = recall;
            if (ids.Count > 0)
                notes.Add($"abstention: expected nothing, got {ids.Count}");
        }
        else
        {
            var found = scenario.ExpectedRecordIds.Count(e => ids.Contains(e, StringComparer.Ordinal));
            recall = (double)found / scenario.ExpectedRecordIds.Count;
            var firstRank = ids.FindIndex(id => scenario.ExpectedRecordIds.Contains(id, StringComparer.Ordinal));
            mrr = firstRank < 0 ? 0 : 1.0 / (firstRank + 1);
        }

        if (leaks > 0) notes.Add($"leaks={leaks}");
        if (untrustedAsFact > 0) notes.Add($"untrustedAsFact={untrustedAsFact}");
        if (supersededAsCurrent > 0) notes.Add($"supersededAsCurrent={supersededAsCurrent}");

        var passed = leaks == 0 && untrustedAsFact == 0 && supersededAsCurrent == 0
                     && recall >= limits.MinRecallAtK && mrr >= limits.MinMrr;
        return new MemoryQualityCase(scenario.Id, scenario.Ability, passed, recall, mrr, leaks, untrustedAsFact, supersededAsCurrent, ids, notes);
    }
}
