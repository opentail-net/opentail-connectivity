// Moved from opentail's OpenTail.Core.Memory (InMemoryIngestionRegistry.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public interface IMemoryIngestionRegistry
{
    bool TryGet(string dedupeKey, out MemoryNormalizedEnvelope envelope, out string? recordId);

    void Upsert(string dedupeKey, MemoryNormalizedEnvelope envelope, string? recordId = null);

    IReadOnlyDictionary<string, MemoryNormalizedEnvelope> SnapshotByDedupeKey();

    IReadOnlyDictionary<string, string> SnapshotDedupeToRecordId();
}

public sealed class InMemoryIngestionRegistry : IMemoryIngestionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MemoryNormalizedEnvelope> _byDedupeKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dedupeToRecordId = new(StringComparer.Ordinal);

    public bool TryGet(string dedupeKey, out MemoryNormalizedEnvelope envelope, out string? recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);

        lock (_gate)
        {
            var foundEnvelope = _byDedupeKey.TryGetValue(dedupeKey, out envelope!);
            var foundRecordId = _dedupeToRecordId.TryGetValue(dedupeKey, out var id);
            recordId = foundRecordId ? id : null;
            return foundEnvelope;
        }
    }

    public void Upsert(string dedupeKey, MemoryNormalizedEnvelope envelope, string? recordId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);
        ArgumentNullException.ThrowIfNull(envelope);

        lock (_gate)
        {
            _byDedupeKey[dedupeKey] = envelope;

            if (!string.IsNullOrWhiteSpace(recordId))
                _dedupeToRecordId[dedupeKey] = recordId!;
        }
    }

    public IReadOnlyDictionary<string, MemoryNormalizedEnvelope> SnapshotByDedupeKey()
    {
        lock (_gate)
            return new Dictionary<string, MemoryNormalizedEnvelope>(_byDedupeKey, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string> SnapshotDedupeToRecordId()
    {
        lock (_gate)
            return new Dictionary<string, string>(_dedupeToRecordId, StringComparer.Ordinal);
    }
}
