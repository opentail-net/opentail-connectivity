namespace OpenTail.Connectivity.Memory;

/// <summary>Why hits were withheld, as counts per reason. Never which records: a caller must not learn what it cannot see.</summary>
public sealed record MemoryRecallResult(
    MemoryRetrievalPlannerAnalysis Analysis,
    IReadOnlyList<MemoryHit> Hits,
    IReadOnlyDictionary<string, int> WithheldByReason,
    IReadOnlyList<string> Notes);

/// <summary>
/// Governed retrieval over any <see cref="IMemoryBackend"/>: analyses the query, runs its subqueries (fusing keyword and vector
/// results when the backend cannot), ranks, and applies <see cref="MemoryGovernance"/> to every hit as the final check.
/// Written once; backends differ only below it. The retrieval half of opentail's MemoryRetrievalPlanner.
/// </summary>
public sealed class MemoryRetriever
{
    private readonly IMemoryBackend _backend;
    private readonly IQueryCondenser? _condenser;
    private readonly IReranker? _reranker;
    private readonly TimeProvider _time;

    public MemoryRetriever(IMemoryBackend backend, IQueryCondenser? condenser = null, IReranker? reranker = null, TimeProvider? time = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _condenser = condenser;
        _reranker = reranker;
        _time = time ?? TimeProvider.System;
    }

    /// <param name="access">Who is asking, and the most they may see. The request can narrow it, never widen it: a requested
    /// permission scope the caller does not hold returns nothing.</param>
    public async Task<MemoryRecallResult> RetrieveAsync(MemoryRetrievalRequest request, MemoryFilter? access = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        MemoryContractValidation.Validate(request);

        if (request.EnableCondenseQuestion && _condenser is not null && !string.IsNullOrWhiteSpace(request.ChatHistory))
        {
            var condensed = await _condenser.CondenseAsync(request.Query, request.ChatHistory, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(condensed))
                request = request with { Query = condensed };
        }

        var analysis = MemoryQueryAnalyzer.Analyze(request);
        var builtFilter = BuildFilter(request, access);
        if (builtFilter.Filter is not { } filter)
        {
            return new MemoryRecallResult(analysis with { AppliedFilters = null }, [], new Dictionary<string, int>(StringComparer.Ordinal) { [builtFilter.Refused ?? "permission-scope"] = 1 },
                [$"intent={analysis.Intent}", "hits=0"]);
        }

        if (Restricts(filter) && !_backend.Capabilities.FiltersBeforeRanking)
        {
            // Filtering after top-k would make recall depend on how many forbidden records outrank the allowed ones.
            throw new InvalidOperationException("This backend cannot filter before ranking, so it cannot serve a query restricted by principal, scope or claim.");
        }

        // With summaries allowed, a record above the caller's sensitivity comes back redacted rather than not at all, so the
        // backend must not drop it; the check below still applies.
        var backendFilter = request.IncludeSummaries ? filter with { MaxSensitivity = null } : filter;
        var limit = Math.Max(20, request.TopK * 4);
        var requested = RequestedSearch(request.RetrievalMode);
        var candidates = new Dictionary<(string Record, string Chunk), Candidate>();
        foreach (var subquery in analysis.Subqueries)
        {
            var hits = await SearchAsync(subquery.Query, limit, backendFilter, requested, cancellationToken).ConfigureAwait(false);
            for (var rank = 0; rank < hits.Count; rank++)
            {
                var hit = hits[rank];
                var key = (hit.Record.Id, hit.ChunkId);
                if (!candidates.TryGetValue(key, out var candidate))
                    candidates[key] = candidate = new Candidate(hit);

                candidate.Score += (hit.Score + 1.0 / (rank + 1)) * subquery.Weight;
                candidate.Reasons.Add($"subquery='{subquery.Query}' rank={rank + 1}");
            }
        }

        // Governance runs before ranking, so a reranker (which may be a remote service) only ever sees what the caller may see.
        var now = _time.GetUtcNow();
        var withheld = new Dictionary<string, int>(StringComparer.Ordinal);
        var sensitivityFree = filter with { MaxSensitivity = null };
        var governed = new List<Candidate>();
        foreach (var candidate in candidates.Values)
        {
            if (Govern(candidate, filter, sensitivityFree, request, now) is { } reason)
            {
                withheld[reason] = withheld.GetValueOrDefault(reason) + 1;
                continue;
            }

            candidate.Score += TypedBoost(candidate.Hit.Record, request, analysis);
            governed.Add(candidate);
        }

        var ranked = governed.OrderByDescending(static c => c.Score).ThenBy(static c => c.Hit.Record.Id, StringComparer.Ordinal).ToList();
        if (_reranker is not null && ranked.Count > 1)
            ranked = await RerankAsync(request.Query, ranked, cancellationToken).ConfigureAwait(false);

        var results = ranked.Take(request.TopK)
            .Select(static c => new MemoryHit(c.Hit.Record, c.Hit.ChunkId, c.Hit.Text, c.Score, MemoryRecall.RoleOf(c.Hit.Record), [.. c.Reasons], c.Disposition))
            .ToList();

        var notes = new List<string>
        {
            $"intent={analysis.Intent}",
            $"subqueries={analysis.Subqueries.Count}",
            $"backend.search={_backend.Capabilities.Search}",
            $"backend.filtersBeforeRanking={_backend.Capabilities.FiltersBeforeRanking}",
            $"candidates={candidates.Count}",
            $"hits={results.Count}",
        };

        return new MemoryRecallResult(analysis, results, withheld, notes);
    }

    /// <summary>
    /// Null when the caller may see the candidate, in which case it is replaced by what they may see; otherwise the reason. Reads
    /// the original record, never a redacted one.
    /// </summary>
    private static string? Govern(Candidate candidate, MemoryFilter filter, MemoryFilter sensitivityFree, MemoryRetrievalRequest request, DateTimeOffset now)
    {
        var record = candidate.Hit.Record;
        if (MemoryGovernance.Deny(record, sensitivityFree, now) is { } reason)
            return reason;

        if (filter.MaxSensitivity is { } max
            && MemoryGovernance.SensitivityRank(record.Policy.Sensitivity) > MemoryGovernance.SensitivityRank(max))
        {
            if (!request.IncludeSummaries)
                return "sensitivity";

            candidate.Disposition = MemoryRetrievalDisposition.Redacted;
            candidate.Reasons.Add("policy: sensitivity above the caller's limit; text withheld");
        }
        else if (!request.IncludeRawEvidence && candidate.Hit.ChunkKind == MemoryChunkKind.RawEvidence)
        {
            if (!request.IncludeSummaries)
                return "raw-evidence";

            candidate.Disposition = MemoryRetrievalDisposition.Summarized;
            candidate.Reasons.Add("policy: raw evidence hidden; summary substituted");
        }

        if (candidate.Disposition != MemoryRetrievalDisposition.Allowed)
        {
            var redacted = MemoryRedaction.Redact(record, candidate.Disposition);
            candidate.Hit = candidate.Hit with { Record = redacted, Text = redacted.Summary ?? string.Empty };
        }

        return null;
    }

    /// <summary>
    /// The search kind a caller asked for explicitly, checked against what the backend can do: an unsupported request fails rather
    /// than quietly becoming another kind of search. <see cref="MemoryRetrievalMode.Auto"/> and the higher-level modes return null
    /// and keep the capability-based choice.
    /// </summary>
    private MemorySearchKinds? RequestedSearch(MemoryRetrievalMode mode)
    {
        var kinds = _backend.Capabilities.Search;
        var (kind, supported) = mode switch
        {
            MemoryRetrievalMode.Keyword => (MemorySearchKinds.Keyword, kinds.HasFlag(MemorySearchKinds.Keyword)),
            MemoryRetrievalMode.Semantic => (MemorySearchKinds.Vector, kinds.HasFlag(MemorySearchKinds.Vector)),
            MemoryRetrievalMode.Hybrid => (MemorySearchKinds.Hybrid,
                kinds.HasFlag(MemorySearchKinds.Hybrid) || (kinds.HasFlag(MemorySearchKinds.Keyword) && kinds.HasFlag(MemorySearchKinds.Vector))),
            _ => (MemorySearchKinds.None, true),
        };
        if (kind == MemorySearchKinds.None)
            return null;
        return supported ? kind : throw new NotSupportedException($"The memory backend cannot do {mode} retrieval (it offers {kinds}).");
    }

    private async Task<IReadOnlyList<BackendHit>> SearchAsync(string query, int limit, MemoryFilter filter, MemorySearchKinds? requested,
        CancellationToken cancellationToken)
    {
        var kinds = _backend.Capabilities.Search;
        if (requested is MemorySearchKinds.Keyword or MemorySearchKinds.Vector)
            return await _backend.SearchAsync(new MemorySearch(query, requested.Value, limit, filter), cancellationToken).ConfigureAwait(false);
        if (kinds.HasFlag(MemorySearchKinds.Hybrid))
            return await _backend.SearchAsync(new MemorySearch(query, MemorySearchKinds.Hybrid, limit, filter), cancellationToken).ConfigureAwait(false);

        if (kinds.HasFlag(MemorySearchKinds.Keyword) && kinds.HasFlag(MemorySearchKinds.Vector))
        {
            var keyword = await _backend.SearchAsync(new MemorySearch(query, MemorySearchKinds.Keyword, limit, filter), cancellationToken).ConfigureAwait(false);
            var vector = await _backend.SearchAsync(new MemorySearch(query, MemorySearchKinds.Vector, limit, filter), cancellationToken).ConfigureAwait(false);
            return ReciprocalRankFusion.Fuse([keyword, vector], limit);
        }

        var single = kinds.HasFlag(MemorySearchKinds.Vector) ? MemorySearchKinds.Vector : MemorySearchKinds.Keyword;
        return await _backend.SearchAsync(new MemorySearch(query, single, limit, filter), cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<Candidate>> RerankAsync(string query, List<Candidate> ranked, CancellationToken cancellationToken)
    {
        var byHit = ranked.ToDictionary(static c => (c.Hit.Record.Id, c.Hit.ChunkId));
        var reordered = await _reranker!.RerankAsync(query, [.. ranked.Select(static c => c.Hit)], ranked.Count, cancellationToken).ConfigureAwait(false);

        // A reranker may only reorder: anything it invents is dropped, anything it leaves out keeps its place after the rest.
        var seen = new HashSet<(string, string)>();
        var result = new List<Candidate>();
        foreach (var hit in reordered)
        {
            var key = (hit.Record.Id, hit.ChunkId);
            if (byHit.TryGetValue(key, out var candidate) && seen.Add(key))
            {
                candidate.Reasons.Add("reranked");
                result.Add(candidate);
            }
        }

        result.AddRange(ranked.Where(c => !seen.Contains((c.Hit.Record.Id, c.Hit.ChunkId))));
        return result;
    }

    // Ranking only, and only from typed fields.
    private static double TypedBoost(MemoryRecord record, MemoryRetrievalRequest request, MemoryRetrievalPlannerAnalysis analysis)
    {
        var boost = 0.0;
        if (analysis.PreferredSources.Contains(record.Source.SourceType))
            boost += 0.06;

        if (request.Labels is { Count: > 0 } && record.Labels is { Count: > 0 } labels)
            boost += Math.Min(0.1, request.Labels.Count(l => labels.Contains(l, StringComparer.OrdinalIgnoreCase)) * 0.03);

        if (analysis.FromUtc is { } from && record.ObservedAtUtc >= from && (analysis.ToUtc is not { } to || record.ObservedAtUtc <= to))
            boost += 0.04;

        if (request.RecordKinds is { Count: > 0 } kinds && kinds.Contains(record.Kind))
            boost += 0.04;

        return boost;
    }

    // The access filter is the ceiling; request values narrow it, and conflicting scopes return nothing.
    private static (MemoryFilter? Filter, string? Refused) BuildFilter(MemoryRetrievalRequest request, MemoryFilter? access)
    {
        var filter = access ?? new MemoryFilter();
        var scopes = filter.Scopes;
        if (!string.IsNullOrWhiteSpace(request.PermissionScope))
        {
            var requested = request.PermissionScope.Trim();
            if (scopes?.Any(s => string.Equals(s?.Trim(), requested, StringComparison.OrdinalIgnoreCase)) != true)
                return (null, "permission-scope");
            scopes = [requested];
        }

        if (!TryNarrowScope(filter.Workspace, request.Workspace, out var workspace)
            || !TryNarrowScope(filter.ProjectScope, request.ProjectScope, out var projectScope)
            || !TryNarrowScope(filter.RepositoryScope, request.RepositoryScope, out var repositoryScope))
            return (null, "scope-conflict");

        return (filter with
        {
            Scopes = scopes,
            Workspace = workspace,
            ProjectScope = projectScope,
            RepositoryScope = repositoryScope,
            MaxSensitivity = Stricter(filter.MaxSensitivity, request.MaxSensitivity),
            MinTrustLevel = filter.MinTrustLevel is { } a && request.MinTrustLevel is { } b ? (a > b ? a : b) : filter.MinTrustLevel ?? request.MinTrustLevel,
            FromUtc = Later(filter.FromUtc, request.FromUtc),
            ToUtc = Earlier(filter.ToUtc, request.ToUtc),
            IncludeSuperseded = filter.IncludeSuperseded && request.IncludeSuperseded,
            IncludeDerived = filter.IncludeDerived && request.IncludeDerived,
        }, null);
    }

    private static bool TryNarrowScope(string? access, string? requested, out string? effective)
    {
        var hasAccess = !string.IsNullOrWhiteSpace(access);
        var hasRequest = !string.IsNullOrWhiteSpace(requested);
        if (hasAccess && hasRequest && !string.Equals(access!.Trim(), requested!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            effective = null;
            return false;
        }
        effective = hasAccess ? access!.Trim() : hasRequest ? requested!.Trim() : null;
        return true;
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null || a >= b ? a : b;

    private static DateTimeOffset? Earlier(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null || a <= b ? a : b;

    private static MemorySensitivity? Stricter(MemorySensitivity? a, MemorySensitivity? b)
        => a is null ? b : b is null ? a : MemoryGovernance.SensitivityRank(a.Value) <= MemoryGovernance.SensitivityRank(b.Value) ? a : b;

    private static bool Restricts(MemoryFilter filter)
        => filter.Principals is { Count: > 0 } || filter.Scopes is { Count: > 0 } || filter.Claims is { Count: > 0 }
           || filter.Collections is { Count: > 0 } || !string.IsNullOrWhiteSpace(filter.Workspace)
           || !string.IsNullOrWhiteSpace(filter.ProjectScope) || !string.IsNullOrWhiteSpace(filter.RepositoryScope);

    private sealed class Candidate(BackendHit hit)
    {
        /// <summary>The hit as the caller may see it: replaced by a redacted copy once governance decides.</summary>
        public BackendHit Hit { get; set; } = hit;

        public double Score { get; set; }

        public MemoryRetrievalDisposition Disposition { get; set; } = MemoryRetrievalDisposition.Allowed;

        public List<string> Reasons { get; } = [];
    }
}

/// <summary>Reciprocal rank fusion of several ranked lists, for backends that cannot fuse keyword and vector results themselves.</summary>
public static class ReciprocalRankFusion
{
    /// <summary>The common RRF constant; it damps the weight of the very top ranks.</summary>
    public const double K = 60.0;

    public static IReadOnlyList<BackendHit> Fuse(IReadOnlyList<IReadOnlyList<BackendHit>> lists, int topK)
    {
        ArgumentNullException.ThrowIfNull(lists);
        var scores = new Dictionary<(string, string), (BackendHit Hit, double Score)>();
        foreach (var list in lists)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var hit = list[rank];
                var key = (hit.Record.Id, hit.ChunkId);
                var add = 1.0 / (K + rank + 1);
                scores[key] = scores.TryGetValue(key, out var existing) ? (existing.Hit, existing.Score + add) : (hit, add);
            }
        }

        return [.. scores.Values
            .OrderByDescending(static s => s.Score)
            .ThenBy(static s => s.Hit.Record.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, topK))
            .Select(static s => s.Hit with { Score = s.Score, Kind = MemorySearchKinds.Hybrid })];
    }
}
