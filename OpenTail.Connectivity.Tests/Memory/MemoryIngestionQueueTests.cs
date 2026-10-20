using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class MemoryIngestionQueueTests
{
    [Fact]
    public async Task EnqueueAsync_ProcessesCapturesAndReturnsQueuedMetadata()
    {
        var pipeline = new RecordingPipeline();
        await using var queue = new MemoryIngestionQueue(
            pipeline,
            new MemoryIngestionQueueOptions(Capacity: 16, MaxBatchSize: 4, DequeueInterval: TimeSpan.Zero));

        var first = await queue.EnqueueAsync(BuildCapture("env-q1", "external-1"));
        var second = await queue.EnqueueAsync(BuildCapture("env-q2", "external-2"));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(first.WasQueued);
        Assert.True(second.WasQueued);
        Assert.NotNull(first.ReplayToken);
        Assert.NotNull(second.ReplayToken);
        Assert.InRange(first.BatchSize ?? 0, 1, 4);
        Assert.InRange(second.BatchSize ?? 0, 1, 4);
        Assert.Equal(2, pipeline.Captures.Count);
    }

    private static MemoryRawCaptureEnvelope BuildCapture(string envelopeId, string externalId)
        => new(
            EnvelopeId: envelopeId,
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: externalId,
            CapturedAtUtc: DateTimeOffset.Parse("2026-06-05T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-06-05T10:00:00Z"),
            RawPayload: $"payload-{externalId}",
            DedupeKey: $"slack:{externalId}",
            ContentHash: $"hash-{externalId}");

    private sealed class RecordingPipeline : IMemoryIngestionPipeline
    {
        public List<MemoryRawCaptureEnvelope> Captures { get; } = [];

        public Task<MemoryIngestionResult> IngestAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
        {
            Captures.Add(capture);
            return Task.FromResult(new MemoryIngestionResult(
                Success: true,
                RecordId: $"record-{capture.EnvelopeId}",
                DedupeKey: capture.DedupeKey));
        }
    }
}
