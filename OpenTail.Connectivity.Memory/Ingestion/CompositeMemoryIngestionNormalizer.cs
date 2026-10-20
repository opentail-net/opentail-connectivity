// Moved from opentail's OpenTail.Core.Memory (CompositeMemoryIngestionNormalizer.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed class CompositeMemoryIngestionNormalizer : IMemoryIngestionNormalizer
{
    private readonly IReadOnlyDictionary<MemoryIngestionSourceType, IMemoryIngestionNormalizer> _normalizers;

    public IReadOnlyCollection<MemoryIngestionSourceType> RegisteredSourceTypes => [.. _normalizers.Keys];

    public CompositeMemoryIngestionNormalizer(IReadOnlyDictionary<MemoryIngestionSourceType, IMemoryIngestionNormalizer> normalizers)
    {
        ArgumentNullException.ThrowIfNull(normalizers);

        if (normalizers.Count == 0)
            throw new ArgumentException("At least one normalizer must be registered.", nameof(normalizers));

        var copy = new Dictionary<MemoryIngestionSourceType, IMemoryIngestionNormalizer>(normalizers.Count);
        foreach (var (sourceType, normalizer) in normalizers)
        {
            if (sourceType == MemoryIngestionSourceType.Unknown)
                throw new ArgumentException("Unknown source type cannot be registered.", nameof(normalizers));

            ArgumentNullException.ThrowIfNull(normalizer);
            copy[sourceType] = normalizer;
        }

        _normalizers = copy;
    }

    public Task<MemoryNormalizedEnvelope> NormalizeAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (capture.SourceType == MemoryIngestionSourceType.Unknown)
            throw new InvalidOperationException("Cannot normalize capture with Unknown source type.");

        if (!_normalizers.TryGetValue(capture.SourceType, out var normalizer))
            throw new InvalidOperationException($"No ingestion normalizer is registered for source type '{capture.SourceType}'.");

        return normalizer.NormalizeAsync(capture, cancellationToken);
    }
}
