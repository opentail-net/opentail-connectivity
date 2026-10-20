// Moved from opentail's OpenTail.Core.Memory (MemoryOperatorControlService.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed class InMemoryMemoryOperatorControlService : IMemoryOperatorControlService
{
    private readonly Dictionary<string, MemoryRecord> _records;
    private readonly Dictionary<string, MemoryEntity> _entities;
    private readonly HashSet<string> _blockedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MemoryOperatorAuditEntry> _audit = [];

    public InMemoryMemoryOperatorControlService(
        IEnumerable<MemoryRecord>? records = null,
        IEnumerable<MemoryEntity>? entities = null)
    {
        _records = (records ?? [])
            .GroupBy(static r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        _entities = (entities ?? [])
            .GroupBy(static e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
    }

    public MemoryOperatorControlSnapshot Snapshot()
        => new(
            Records: new Dictionary<string, MemoryRecord>(_records, StringComparer.Ordinal),
            Entities: new Dictionary<string, MemoryEntity>(_entities, StringComparer.Ordinal),
            BlockedSources: new HashSet<string>(_blockedSources, StringComparer.OrdinalIgnoreCase),
            PinnedRecordIds: new HashSet<string>(_pinned, StringComparer.OrdinalIgnoreCase),
            AuditTrail: [.. _audit]);

    public MemoryOperatorActionResult Apply(MemoryOperatorActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Actor))
            throw new ArgumentException("Operator actor is required.", nameof(request));

        var (success, message, metadata) = request.Kind switch
        {
            MemoryOperatorActionKind.Pin => Pin(request),
            MemoryOperatorActionKind.Unpin => Unpin(request),
            MemoryOperatorActionKind.Forget => Forget(request),
            MemoryOperatorActionKind.Archive => Archive(request),
            MemoryOperatorActionKind.ReEmbed => ReEmbed(request),
            MemoryOperatorActionKind.MergeDuplicates => MergeDuplicates(request),
            MemoryOperatorActionKind.RelabelEntity => RelabelEntity(request),
            MemoryOperatorActionKind.MarkStale => MarkStale(request),
            MemoryOperatorActionKind.MarkCanonical => MarkCanonical(request),
            MemoryOperatorActionKind.BlockSource => BlockSource(request),
            MemoryOperatorActionKind.UnblockSource => UnblockSource(request),
            MemoryOperatorActionKind.UpdateRetention => UpdateRetention(request),
            _ => (false, $"Unsupported operator action '{request.Kind}'.", null),
        };

        var audit = CreateAudit(request, success, message, metadata);
        _audit.Add(audit);

        return new MemoryOperatorActionResult(
            Success: success,
            Message: message,
            Snapshot: Snapshot(),
            Audit: audit);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) Pin(MemoryOperatorActionRequest request)
    {
        if (!TryGetRecord(request.RecordId, out _))
            return FailMissingRecord(request.RecordId, "pin");

        _pinned.Add(request.RecordId!);
        return (true, $"Pinned memory record '{request.RecordId}'.", null);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) Unpin(MemoryOperatorActionRequest request)
    {
        if (!TryGetRecord(request.RecordId, out _))
            return FailMissingRecord(request.RecordId, "unpin");

        _pinned.Remove(request.RecordId!);
        return (true, $"Unpinned memory record '{request.RecordId}'.", null);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) Forget(MemoryOperatorActionRequest request)
    {
        if (!TryGetRecord(request.RecordId, out _))
            return FailMissingRecord(request.RecordId, "forget");

        _records.Remove(request.RecordId!);
        _pinned.Remove(request.RecordId!);
        return (true, $"Forgot memory record '{request.RecordId}'.", null);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) Archive(MemoryOperatorActionRequest request)
    {
        if (!TryGetRecord(request.RecordId, out var record))
            return FailMissingRecord(request.RecordId, "archive");

        _records[record.Id] = record with
        {
            DeletedAtUtc = request.TimestampUtc ?? DateTimeOffset.UtcNow,
            DeletedReason = string.IsNullOrWhiteSpace(request.Note) ? "operator-archive" : request.Note,
        };

        return (true, $"Archived memory record '{request.RecordId}'.", null);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) ReEmbed(MemoryOperatorActionRequest request)
    {
        if (!TryGetRecord(request.RecordId, out var record))
            return FailMissingRecord(request.RecordId, "re-embed");

        var revision = string.IsNullOrWhiteSpace(request.EmbeddingRevision)
            ? DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            : request.EmbeddingRevision!.Trim();

        var embedding = record.Embedding;
        if (embedding is null)
        {
            embedding = new MemoryEmbeddingRef(
                Model: new MemoryEmbeddingModel("operator", "reembed", revision, 0, "cosine"),
                VectorId: $"reembed-{record.Id}");
        }
        else
        {
            embedding = embedding with
            {
                Model = embedding.Model with { Revision = revision }
            };
        }

        _records[record.Id] = record with { Embedding = embedding };
        return (true, $"Re-embedded memory record '{request.RecordId}' with revision '{revision}'.", null);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) MergeDuplicates(MemoryOperatorActionRequest request)
    {
        if (!TryGetRecord(request.RecordId, out var primary))
            return FailMissingRecord(request.RecordId, "merge-duplicates");
        if (!TryGetRecord(request.SecondaryRecordId, out var secondary))
            return FailMissingRecord(request.SecondaryRecordId, "merge-duplicates");

        var mergedLabels = (primary.Labels ?? [])
            .Concat(secondary.Labels ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mergedEntityRefs = (primary.EntityRefs ?? [])
            .Concat(secondary.EntityRefs ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _records[primary.Id] = primary with
        {
            Labels = mergedLabels,
            EntityRefs = mergedEntityRefs,
            IsDerived = true,
            DerivedFromRecordIds = (primary.DerivedFromRecordIds ?? [])
                .Concat([secondary.Id])
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };

        _records.Remove(secondary.Id);
        _pinned.Remove(secondary.Id);

        return (true, $"Merged duplicate '{secondary.Id}' into '{primary.Id}'.", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["primaryId"] = primary.Id,
            ["removedId"] = secondary.Id,
        });
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) RelabelEntity(MemoryOperatorActionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EntityId) || !_entities.TryGetValue(request.EntityId, out var entity))
            return (false, $"Cannot relabel entity '{request.EntityId ?? "(null)"}' because it does not exist.", null);
        if (string.IsNullOrWhiteSpace(request.Label))
            return (false, "Cannot relabel entity because label is empty.", null);

        var tags = (entity.Tags ?? [])
            .Concat([request.Label.Trim()])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _entities[entity.Id] = entity with { Tags = tags };

        return (true, $"Relabeled entity '{entity.Id}' with '{request.Label}'.", null);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) MarkStale(MemoryOperatorActionRequest request)
    {
        if (!TryGetRecord(request.RecordId, out var record))
            return FailMissingRecord(request.RecordId, "mark-stale");

        _records[record.Id] = record with
        {
            IsSuperseded = true,
            SupersededAtUtc = request.TimestampUtc ?? DateTimeOffset.UtcNow,
        };

        return (true, $"Marked memory record '{record.Id}' as stale/superseded.", null);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) MarkCanonical(MemoryOperatorActionRequest request)
    {
        if (!TryGetRecord(request.RecordId, out var record))
            return FailMissingRecord(request.RecordId, "mark-canonical");

        var labels = (record.Labels ?? [])
            .Concat(["canonical"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _records[record.Id] = record with
        {
            IsSuperseded = false,
            SupersededAtUtc = null,
            Labels = labels,
        };

        return (true, $"Marked memory record '{record.Id}' as canonical.", null);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) BlockSource(MemoryOperatorActionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceSystem))
            return (false, "Cannot block source because source system is empty.", null);

        _blockedSources.Add(request.SourceSystem.Trim());
        return (true, $"Blocked memory source '{request.SourceSystem}'.", null);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) UnblockSource(MemoryOperatorActionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceSystem))
            return (false, "Cannot unblock source because source system is empty.", null);

        _blockedSources.Remove(request.SourceSystem.Trim());
        return (true, $"Unblocked memory source '{request.SourceSystem}'.", null);
    }

    private (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) UpdateRetention(MemoryOperatorActionRequest request)
    {
        if (!TryGetRecord(request.RecordId, out var record))
            return FailMissingRecord(request.RecordId, "update-retention");
        if (request.RetentionClass is null)
            return (false, "Cannot update retention because retention class is missing.", null);

        _records[record.Id] = record with
        {
            Policy = record.Policy with
            {
                RetentionClass = request.RetentionClass.Value,
            }
        };

        return (true, $"Updated retention for '{record.Id}' to '{request.RetentionClass}'.", null);
    }

    private MemoryOperatorAuditEntry CreateAudit(
        MemoryOperatorActionRequest request,
        bool success,
        string message,
        IReadOnlyDictionary<string, string>? metadata)
        => new(
            AuditId: Guid.NewGuid().ToString("N"),
            OccurredAtUtc: request.TimestampUtc ?? DateTimeOffset.UtcNow,
            Actor: request.Actor.Trim(),
            Action: request.Kind,
            Success: success,
            Message: message,
            Metadata: metadata);

    private bool TryGetRecord(string? recordId, out MemoryRecord record)
    {
        if (!string.IsNullOrWhiteSpace(recordId) && _records.TryGetValue(recordId, out record!))
            return true;

        record = null!;
        return false;
    }

    private static (bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata) FailMissingRecord(string? recordId, string action)
        => (false, $"Cannot {action} record '{recordId ?? "(null)"}' because it does not exist.", null);
}
