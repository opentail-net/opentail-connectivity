namespace OpenTail.Connectivity.Memory;

/// <summary>
/// A backend held in memory: keyword search by term overlap, filters applied before ranking. For tests, samples and small
/// single-process deployments; it is also the reference the conformance suite is written against.
/// </summary>
public sealed class InMemoryMemoryBackend : IMemoryBackend
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (MemoryRecord Record, IReadOnlyList<MemoryChunk> Chunks)> _records = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public InMemoryMemoryBackend(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public MemoryBackendCapabilities Capabilities { get; } = new(
        Search: MemorySearchKinds.Keyword,
        FiltersBeforeRanking: true,
        TemporalValidity: true,
        DeleteBySource: true);

    public Task UpsertAsync(MemoryRecord record, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Any(c => !string.Equals(c.ParentRecordId, record.Id, StringComparison.Ordinal)))
            throw new ArgumentException("Every chunk must belong to the record.", nameof(chunks));

        lock (_gate)
            _records[record.Id] = (record, [.. chunks]);
        return Task.CompletedTask;
    }

    public Task<MemoryRecord?> GetAsync(string recordId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_records.TryGetValue(recordId, out var entry) ? entry.Record : null);
    }

    public Task<int> DeleteAsync(MemoryDeletion deletion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        lock (_gate)
        {
            var doomed = _records.Values
                .Where(e => deletion.RecordId is { } id ? e.Record.Id == id
                    : deletion.DocumentId is { } doc ? e.Record.Source.DocumentId == doc
                    : deletion.Collection is { } col && string.Equals(e.Record.Collection, col, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Record.Id)
                .ToList();
            foreach (var id in doomed)
                _records.Remove(id);
            return Task.FromResult(doomed.Count);
        }
    }

    public Task<IReadOnlyList<BackendHit>> SearchAsync(MemorySearch search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var terms = Terms(search.Query);
        var now = _time.GetUtcNow();
        List<BackendHit> hits;
        lock (_gate)
        {
            hits = [.. _records.Values
                .Where(e => MemoryGovernance.Deny(e.Record, search.Filter, now) is null)
                .SelectMany(e => e.Chunks.Count > 0
                    ? e.Chunks.Select(c => (e.Record, c.Id, c.Kind, Text: c.Content))
                    : [(e.Record, e.Record.Id, MemoryChunkKind.Unknown, Text: e.Record.Content)])
                .Select(x => (x.Record, x.Id, x.Kind, x.Text, Score: Overlap(terms, x.Text)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Record.Id, StringComparer.Ordinal)
                .Take(Math.Max(0, search.TopK))
                .Select(x => new BackendHit(x.Record, x.Id, x.Kind, x.Text, x.Score, MemorySearchKinds.Keyword))];
        }

        return Task.FromResult<IReadOnlyList<BackendHit>>(hits);
    }

    private static HashSet<string> Terms(string text)
        => [.. Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(static m => m.Value)];

    private static double Overlap(HashSet<string> query, string text)
    {
        if (query.Count == 0)
            return 0;
        var terms = Terms(text);
        return (double)query.Count(terms.Contains) / query.Count;
    }
}
