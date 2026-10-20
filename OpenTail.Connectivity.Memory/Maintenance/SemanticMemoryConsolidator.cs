namespace OpenTail.Connectivity.Memory;

/// <summary>
/// Suggests duplicate pairs by searching the backend with each record's text. Adapted from opentail's consolidator (which used
/// IMemoryStore): it only pairs records with the same permission scope, claims and taint, because merging across them would let
/// one record's content reach readers of the other or wash out an untrusted mark. Suggestions only; nothing is merged here.
/// </summary>
public sealed class SemanticMemoryConsolidator(IMemoryBackend backend) : IMemoryConsolidator
{
    public async Task<IReadOnlyList<MemoryMergeSuggestion>> FindDuplicatesAsync(
        IReadOnlyList<MemoryRecord> candidates,
        SemanticConsolidationOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var opt = options ?? new SemanticConsolidationOptions();
        var byId = candidates.ToDictionary(static r => r.Id, StringComparer.Ordinal);
        var suggestions = new List<MemoryMergeSuggestion>();
        var seenPairs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var batch in candidates.Where(static r => !string.IsNullOrWhiteSpace(r.Content)).Chunk(opt.BatchSize))
        {
            foreach (var record in batch)
            {
                if (suggestions.Count >= opt.MaxCandidatePairs)
                    return suggestions;

                ct.ThrowIfCancellationRequested();

                // Search as a reader of exactly this record's audience, so peers outside it never come back.
                var filter = new MemoryFilter(
                    Scopes: record.Policy.PermissionScope is { } scope ? [scope] : null,
                    Claims: record.Policy.RequiredClaims,
                    Principals: record.Source.Acl?.Principals,
                    IncludeSuperseded: false);
                var hits = await backend.SearchAsync(new MemorySearch(record.Content, backend.Capabilities.Search.HasFlag(MemorySearchKinds.Hybrid)
                    ? MemorySearchKinds.Hybrid : backend.Capabilities.Search.HasFlag(MemorySearchKinds.Vector)
                    ? MemorySearchKinds.Vector : MemorySearchKinds.Keyword, 5, filter), ct).ConfigureAwait(false);

                foreach (var hit in hits)
                {
                    var peer = hit.Record;
                    if (string.Equals(peer.Id, record.Id, StringComparison.Ordinal) || !byId.ContainsKey(peer.Id))
                        continue;

                    if (hit.Score > 0.0 && hit.Score < opt.SimilarityThreshold)
                        continue;

                    if (!SameAudience(record, peer))
                        continue;

                    var pairKey = string.CompareOrdinal(record.Id, peer.Id) < 0 ? $"{record.Id}|{peer.Id}" : $"{peer.Id}|{record.Id}";
                    if (!seenPairs.Add(pairKey))
                        continue;

                    var recordIsCanonical = record.BaseImportance > peer.BaseImportance
                        || (Math.Abs(record.BaseImportance - peer.BaseImportance) < 0.001 && record.CreatedAtUtc < peer.CreatedAtUtc);

                    suggestions.Add(new MemoryMergeSuggestion
                    {
                        CanonicalId = recordIsCanonical ? record.Id : peer.Id,
                        DuplicateId = recordIsCanonical ? peer.Id : record.Id,
                        Similarity = hit.Score,
                        Rationale = "Semantic similarity match within the same audience and taint",
                    });
                }
            }
        }

        return suggestions;
    }

    private static bool SameAudience(MemoryRecord a, MemoryRecord b)
        => string.Equals(a.Policy.PermissionScope?.Trim(), b.Policy.PermissionScope?.Trim(), StringComparison.OrdinalIgnoreCase)
           && a.Policy.Tainted == b.Policy.Tainted
           && a.Policy.Sensitivity == b.Policy.Sensitivity
           && SameSet(a.Policy.RequiredClaims, b.Policy.RequiredClaims)
           && SameSet(a.Source.Acl?.Principals, b.Source.Acl?.Principals);

    private static bool SameSet(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        => (a ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(b ?? []);
}
