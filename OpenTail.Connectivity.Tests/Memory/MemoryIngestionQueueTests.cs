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

    [Fact]
    public async Task A_capture_dropped_on_overflow_answers_its_caller_at_once()
    {
        var pipeline = new GatedPipeline();
        await using var queue = new MemoryIngestionQueue(pipeline,
            new MemoryIngestionQueueOptions(Capacity: 1, MaxBatchSize: 1, DequeueInterval: TimeSpan.Zero, DropOldestOnOverflow: true));

        var a = queue.EnqueueAsync(BuildCapture("a", "a"));
        await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the worker holds a
        var b = queue.EnqueueAsync(BuildCapture("b", "b"));             // waits in the channel
        var c = queue.EnqueueAsync(BuildCapture("c", "c"));             // pushes b out

        var dropped = await b.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(dropped.Success);
        Assert.Contains("Dropped", dropped.Error);

        pipeline.Release.SetResult();
        Assert.True((await a).Success);
        Assert.True((await c).Success);
    }

    [Fact]
    public async Task Disposing_answers_every_caller_still_waiting()
    {
        var pipeline = new GatedPipeline();
        var queue = new MemoryIngestionQueue(pipeline, new MemoryIngestionQueueOptions(Capacity: 4, MaxBatchSize: 1, DequeueInterval: TimeSpan.Zero));

        var running = queue.EnqueueAsync(BuildCapture("a", "a"));
        await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = queue.EnqueueAsync(BuildCapture("b", "b"));

        await queue.DisposeAsync();

        foreach (var result in await Task.WhenAll(running, waiting).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.False(result.Success);
            Assert.Contains("disposed", result.Error);
        }
    }

    /// <summary>Holds the first capture until released (or cancelled), so others queue up behind it.</summary>
    private sealed class GatedPipeline : IMemoryIngestionPipeline
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MemoryIngestionResult> IngestAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new MemoryIngestionResult(Success: true, RecordId: capture.EnvelopeId, DedupeKey: capture.DedupeKey);
        }
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
