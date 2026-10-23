// Moved from opentail's OpenTail.Core.Memory (MemoryIngestionQueue.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed class MemoryIngestionQueue : IAsyncDisposable
{
    private readonly IMemoryIngestionPipeline _pipeline;
    private readonly MemoryIngestionQueueOptions _options;
    private readonly Channel<QueuedCapture> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public MemoryIngestionQueue(
        IMemoryIngestionPipeline pipeline,
        MemoryIngestionQueueOptions? options = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _options = options ?? new MemoryIngestionQueueOptions();

        if (_options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Queue capacity must be greater than zero.");

        _channel = Channel.CreateBounded<QueuedCapture>(new BoundedChannelOptions(_options.Capacity)
        {
            FullMode = _options.DropOldestOnOverflow ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        }, itemDropped: queued => queued.Promise.TrySetResult(Failed(queued,
            "Dropped: the ingestion queue was full and DropOldestOnOverflow is set, so the oldest capture made room.")));

        _worker = Task.Run(ProcessAsync);
    }

    public async Task<MemoryIngestionResult> EnqueueAsync(
        MemoryRawCaptureEnvelope capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var tcs = new TaskCompletionSource<MemoryIngestionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayToken = BuildReplayToken(capture);
        var queued = new QueuedCapture(capture, replayToken, tcs);

        await _channel.Writer.WriteAsync(queued, cancellationToken);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            
            var result = await tcs.Task.WaitAsync(timeoutCts.Token);
            return result with
            {
                WasQueued = true,
                ReplayToken = replayToken,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MemoryIngestionResult(
                Success: false,
                DedupeKey: capture.DedupeKey,
                WasQueued: true,
                ReplayToken: replayToken,
                BatchSize: 0,
                Error: "Ingestion queue processing timed out after 30 seconds.");
        }
    }

    private async Task ProcessAsync()
    {
        var delay = _options.DequeueInterval ?? TimeSpan.FromMilliseconds(10);

        while (await _channel.Reader.WaitToReadAsync(_cts.Token))
        {
            var batch = new List<QueuedCapture>(Math.Max(1, _options.MaxBatchSize));
            while (batch.Count < Math.Max(1, _options.MaxBatchSize)
                   && _channel.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            foreach (var queued in batch)
            {
                try
                {
                    var result = await _pipeline.IngestAsync(queued.Capture, _cts.Token);
                    queued.Promise.TrySetResult(result with
                    {
                        WasQueued = true,
                        ReplayToken = queued.ReplayToken,
                        BatchSize = batch.Count,
                    });
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    queued.Promise.TrySetResult(Failed(queued, Disposed));
                }
                catch (OperationCanceledException oce)
                {
                    queued.Promise.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    queued.Promise.TrySetResult(new MemoryIngestionResult(
                        Success: false,
                        DedupeKey: queued.Capture.DedupeKey,
                        WasQueued: true,
                        ReplayToken: queued.ReplayToken,
                        BatchSize: batch.Count,
                        Error: ex.Message));
                }
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _cts.Token);
        }
    }

    private static string BuildReplayToken(MemoryRawCaptureEnvelope capture)
    {
        var identity = $"{capture.SourceSystem}:{capture.ExternalId}:{capture.ObservedAtUtc:O}:{capture.ContentHash}:{capture.DedupeKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash)[..24];
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
            // expected on dispose
        }
        finally
        {
            // Every caller still waiting gets an answer, never the 30-second timeout.
            while (_channel.Reader.TryRead(out var left))
                left.Promise.TrySetResult(Failed(left, Disposed));
            _cts.Dispose();
        }
    }

    private const string Disposed = "The ingestion queue was disposed before this capture was processed.";

    private static MemoryIngestionResult Failed(QueuedCapture queued, string error) => new(
        Success: false,
        DedupeKey: queued.Capture.DedupeKey,
        WasQueued: true,
        ReplayToken: queued.ReplayToken,
        BatchSize: 0,
        Error: error);

    private sealed record QueuedCapture(
        MemoryRawCaptureEnvelope Capture,
        string ReplayToken,
        TaskCompletionSource<MemoryIngestionResult> Promise);
}
