using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace OpenTail.Connectivity.Queues.RabbitMQ;

/// <summary>
/// <see cref="IWorkQueue"/> on RabbitMQ. Each work queue is a durable quorum queue, with a quorum queue beside it for dead
/// letters and "wait" queues for delays. Every publish waits for the broker's confirmation, and a message is acknowledged only
/// after its replacement is confirmed, so a crash can duplicate a job but never lose one.
/// </summary>
/// <remarks>
/// <para><b>Leases.</b> A leased job is an unacknowledged delivery on this instance's channel. If the process dies, the broker
/// hands the job out again. If the lease runs out while the process lives, this instance gives the job back itself (checked every
/// second and before each lease), as a copy with the attempt counted. RabbitMQ closes a channel that holds a delivery longer than
/// its <c>consumer_timeout</c> (30 minutes by default), so a lease, extensions included, is capped at
/// <see cref="RabbitMqWorkQueueOptions.MaxLeaseTotal"/>.</para>
/// <para><b>Delays.</b> RabbitMQ has no delayed delivery without a plugin. A delayed job waits in a queue whose messages all live
/// for the same time (powers of 4 seconds, from 1 second to about 3 days) and then return to the work queue; a job that comes back
/// before its time waits again. A delayed job is never handed out early, and is at most about a second late.</para>
/// <para><b>Keys</b> are carried, not deduplicated: RabbitMQ keeps no index of past messages. Handlers must be idempotent anyway
/// (delivery is at least once); use the key for that.</para>
/// <para><b>Cancelling, concurrency limits and circuits</b> need <see cref="RabbitMqWorkQueueOptions.Control"/>, a store every
/// worker shares (<see cref="IQueueControl"/>): RabbitMQ cannot
/// remove a message from the middle of a queue, so a cancelled job is dropped when a worker meets it (leasing, settling, or
/// renewing its lease). A job over its key's limit, or whose circuit is open, waits a few seconds in a wait queue and is looked at
/// again; for a large backlog on one resource, a queue of its own with as many consumers as the limit churns less. Without the
/// store, <see cref="CancelAsync"/> throws and jobs with a concurrency key are refused. A cancel of an unknown id is recorded and returns true.</para>
/// <para><b>Listing</b> (<see cref="PendingAsync"/>) shows waiting and delayed jobs, not jobs other workers hold. Reading a queue
/// means taking its messages, so waiting jobs are listed by replacing each with a copy (attempts unchanged); listed jobs move to the
/// back of the queue.</para>
/// </remarks>
public sealed class RabbitMqWorkQueue : IWorkQueue
{
    /// <summary>What this queue guarantees; known without connecting.</summary>
    public static WorkQueueCapabilities SupportedCapabilities { get; } =
        new(Durable: true, DelayedDelivery: true, LeaseExpiry: true, DeduplicatesKeys: false, Cancellation: false, ListsRunningJobs: false,
            ConcurrencyLimits: false);

    /// <summary>What this queue guarantees with a shared <see cref="IQueueControl"/> store.</summary>
    public static WorkQueueCapabilities CapabilitiesWithControl { get; } = SupportedCapabilities with { Cancellation = true, ConcurrencyLimits = true };

    // A job over its key's limit, or behind an open circuit, waits this long before it is looked at again.
    private const long ParkMs = 4_000;

    // At most this many jobs are moved to wait per lease call, so one busy key does not churn the whole queue at once.
    private const int MaxParksPerLease = 20;

    private const string Prefix = "x-ot-";
    private static readonly long[] WaitTiersSeconds = [1, 4, 16, 64, 256, 1024, 4096, 16384, 65536, 262144];

    private readonly ConnectionFactory? _factory;
    private readonly bool _ownsConnection;
    private readonly WorkQueueOptions _options;
    private readonly RabbitMqWorkQueueOptions _rabbit;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private IConnection? _connection;
    private IChannel? _channel;
    private Task? _sweeper;

    /// <param name="uri">For example <c>amqp://user:password@host:5672/vhost</c>. Connects on first use.</param>
    public RabbitMqWorkQueue(Uri uri, WorkQueueOptions? options = null, RabbitMqWorkQueueOptions? rabbit = null, TimeProvider? time = null)
        : this(options, rabbit, time)
    {
        ArgumentNullException.ThrowIfNull(uri);
        _factory = new ConnectionFactory { Uri = uri, ClientProvidedName = "opentail-work-queue" };
        _ownsConnection = true;
    }

    /// <param name="connection">An open connection; this queue opens its own channels on it and leaves the connection open.</param>
    public RabbitMqWorkQueue(IConnection connection, WorkQueueOptions? options = null, RabbitMqWorkQueueOptions? rabbit = null, TimeProvider? time = null)
        : this(options, rabbit, time)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    private RabbitMqWorkQueue(WorkQueueOptions? options, RabbitMqWorkQueueOptions? rabbit, TimeProvider? time)
    {
        _options = options ?? new WorkQueueOptions();
        _rabbit = rabbit ?? new RabbitMqWorkQueueOptions();
        _time = time ?? TimeProvider.System;
    }

    public WorkQueueCapabilities Capabilities => _rabbit.Control is null ? SupportedCapabilities : CapabilitiesWithControl;

    public async Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default)
    {
        _options.Validate(request);
        var now = Now;
        var job = new Message(Guid.NewGuid().ToString("N"), request.Tenant, request.Body, request.Key,
            WorkQueueOptions.HeadersWithTrace(request),
            Attempts: 0, EnqueuedAt: now, NotBefore: request.NotBefore?.ToUnixTimeMilliseconds() ?? now, LastError: null, Source: request.Source,
            ConcurrencyKey: request.ConcurrencyKey, MaxRunMs: request.MaxRunTime is { } run ? (long)run.TotalMilliseconds : null);
        if (request.ConcurrencyKey is not null && _rabbit.Control is null)
            throw new NotSupportedException("Concurrency keys on RabbitMQ need RabbitMqWorkQueueOptions.Control, a store all workers share.");
        await LockedAsync(async channel =>
        {
            await DeclareAsync(channel, request.Queue, ct).ConfigureAwait(false);
            await PublishDueOrWaitingAsync(channel, request.Queue, job, now, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        return new EnqueueResult(job.Id, Duplicate: false);
    }

    public Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, CancellationToken ct = default)
        => LeaseAsync(queue, max, lease, [], ct);

    public Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, IReadOnlyCollection<string> skipConcurrencyKeys,
        CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        WorkQueueOptions.ValidateLease(max, lease);
        ArgumentNullException.ThrowIfNull(skipConcurrencyKeys);
        if (lease > _rabbit.MaxLeaseTotalOrDefault)
            throw new ArgumentOutOfRangeException(nameof(lease), $"A RabbitMQ lease is at most {_rabbit.MaxLeaseTotalOrDefault} (the broker's consumer_timeout).");

        return LockedAsync<IReadOnlyList<LeasedJob>>(async channel =>
        {
            await DeclareAsync(channel, queue, ct).ConfigureAwait(false);
            await ReturnExpiredAsync(channel, ct).ConfigureAwait(false);
            var leased = new List<LeasedJob>();
            var parks = 0;
            while (leased.Count < max && parks < MaxParksPerLease)
            {
                var got = await channel.BasicGetAsync(Name(queue), autoAck: false, ct).ConfigureAwait(false);
                if (got is null)
                    break;
                var message = Read(got);
                var now = Now;
                if (await IsCancelledAsync(queue, message.Id, ct).ConfigureAwait(false))
                {
                    await channel.BasicAckAsync(got.DeliveryTag, multiple: false, ct).ConfigureAwait(false); // dropped
                    continue;
                }

                if (message.NotBefore > now)
                {
                    // Back from a wait queue a little early: wait again.
                    await PublishDueOrWaitingAsync(channel, queue, message, now, ct).ConfigureAwait(false);
                    await channel.BasicAckAsync(got.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
                    continue;
                }

                // A job past its attempt or age limit goes to the dead letters before it takes a slot, a start or a probe.
                if (WorkQueueRules.DeadReason(message.Attempts, At(message.EnqueuedAt), At(now), _options) is { } reason)
                {
                    await PublishDeadAsync(channel, queue, message, reason, now, ct).ConfigureAwait(false);
                    await channel.BasicAckAsync(got.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
                    continue;
                }

                var token = Guid.NewGuid().ToString("N");
                var until = now + (long)lease.TotalMilliseconds;
                if (message.ConcurrencyKey is { } ck && _rabbit.Control is { } control)
                {
                    // A slot, then the circuit, then the rate: any refusing means the job waits a while, using no attempt. The rate
                    // goes last, so only a job that actually starts counts as a start.
                    var release = !skipConcurrencyKeys.Contains(ck)
                                  && await control.TryTakeSlotAsync(queue, ck, _options.LimitFor(ck) ?? int.MaxValue, token, At(until), ct).ConfigureAwait(false);
                    var probed = false;
                    if (release)
                    {
                        var allowed = false;
                        await control.UpdateCircuitAsync(queue, ck, c =>
                        {
                            var (ok, probe) = Circuits.MayRelease(c, now, _options);
                            allowed = ok;
                            probed = ok && probe;
                            return probed ? Circuits.ProbeReleased(c!, token, until) : c;
                        }, ct).ConfigureAwait(false);
                        if (!allowed)
                        {
                            await control.ReleaseSlotAsync(queue, ck, token, ct).ConfigureAwait(false);
                            release = false;
                        }
                    }
                    if (release && _options.RateFor(ck) is { } rate && !await control.TryTakeStartAsync(queue, ck, rate, At(now), ct).ConfigureAwait(false))
                    {
                        await control.ReleaseSlotAsync(queue, ck, token, ct).ConfigureAwait(false);
                        if (probed)
                            await control.UpdateCircuitAsync(queue, ck, c => Circuits.Settled(c, token), ct).ConfigureAwait(false); // the probe never ran
                        release = false;
                    }

                    if (!release)
                    {
                        await PublishDueOrWaitingAsync(channel, queue, message with { NotBefore = now + ParkMs }, now, ct).ConfigureAwait(false);
                        await channel.BasicAckAsync(got.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
                        parks++;
                        continue;
                    }
                }

                var job = message with { Attempts = message.Attempts + 1 };
                _leases[token] = new Lease(queue, job, got.DeliveryTag, channel, now, until, token);
                leased.Add(new LeasedJob(queue, job.Id, job.Tenant, job.Body, job.Key, job.Headers, job.Attempts, At(job.EnqueuedAt), At(until), token,
                    job.Source, job.ConcurrencyKey, RunTime(job.MaxRunMs)));
            }

            return leased;
        }, ct);
    }

    public Task<bool> CompleteAsync(LeasedJob job, CancellationToken ct = default)
        => WithLeaseAsync(job, async (channel, lease) =>
        {
            await channel.BasicAckAsync(lease.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
            await SettleKeyAsync(lease, _ => null, ct).ConfigureAwait(false); // the resource worked
        }, ct);

    public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error = null, CancellationToken ct = default)
        => RetryAsync(job, delay, error, resourceFailure: false, ct);

    public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error, bool resourceFailure, CancellationToken ct = default)
        => WithLeaseAsync(job, async (channel, lease) =>
        {
            var now = Now;
            var again = lease.Job with { NotBefore = now + (long)Math.Max(0, delay.TotalMilliseconds), LastError = error };
            await PublishDueOrWaitingAsync(channel, lease.Queue, again, now, ct).ConfigureAwait(false);
            await channel.BasicAckAsync(lease.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
            await SettleKeyAsync(lease, c => resourceFailure ? Circuits.ResourceFailed(c, now, _options) : Circuits.Settled(c, lease.Token), ct)
                .ConfigureAwait(false);
        }, ct);

    public Task<bool> DeadLetterAsync(LeasedJob job, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return WithLeaseAsync(job, async (channel, lease) =>
        {
            await PublishDeadAsync(channel, lease.Queue, lease.Job, reason, Now, ct).ConfigureAwait(false);
            await channel.BasicAckAsync(lease.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
            await SettleKeyAsync(lease, c => Circuits.Settled(c, lease.Token), ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>The progress note is not kept: RabbitMQ cannot list running jobs, so nothing would show it.</summary>
    public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, string? progress, DateTimeOffset? lastHeartbeat, CancellationToken ct = default)
        => ExtendAsync(job, lease, ct);

    public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        return WithLeaseAsync(job, async (_, held) =>
        {
            var until = Now + (long)lease.TotalMilliseconds;
            if (until - held.LeasedAt > (long)_rabbit.MaxLeaseTotalOrDefault.TotalMilliseconds)
                throw new ArgumentOutOfRangeException(nameof(lease), $"A RabbitMQ lease, extensions included, is at most {_rabbit.MaxLeaseTotalOrDefault} (the broker's consumer_timeout).");
            held.Until = until;
            if (held.Job.ConcurrencyKey is { } key && _rabbit.Control is { } control)
                await control.RenewSlotAsync(held.Queue, key, held.Token, At(until), ct).ConfigureAwait(false);
        }, ct, keepLease: true);
    }

    /// <summary>Reads without removing: the messages are taken on a channel of their own, which is closed to put them back.</summary>
    public async Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(string queue, int max = 100, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);
        await LockedAsync(async channel =>
        {
            await DeclareAsync(channel, queue, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        await using var peek = await connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);
        var dead = new List<DeadLetter>();
        while (dead.Count < max && await peek.BasicGetAsync(DeadName(queue), autoAck: false, ct).ConfigureAwait(false) is { } got)
        {
            var m = Read(got, countReturned: false); // peeking returns them, which is not an attempt
            dead.Add(new DeadLetter(queue, m.Id, m.Tenant, m.Body, m.Key, m.Headers, m.Attempts, At(m.EnqueuedAt),
                At(LongHeader(got.BasicProperties.Headers, "dead-at") ?? 0), StringHeader(got.BasicProperties.Headers, "reason") ?? "the broker gave up after too many deliveries (the worker kept crashing)", m.LastError, m.Source,
                m.ConcurrencyKey));
        }

        await peek.CloseAsync(ct).ConfigureAwait(false);
        return dead;
    }

    public Task<bool> RedriveAsync(string queue, string id, CancellationToken ct = default)
        => RedriveAsync(queue, id, _time.GetUtcNow(), ct);

    public async Task<bool> RedriveAsync(string queue, string id, DateTimeOffset notBefore, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);
        await LockedAsync(async channel =>
        {
            await DeclareAsync(channel, queue, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        await using var search = await connection.CreateChannelAsync(Confirmed, ct).ConfigureAwait(false);
        var found = false;
        while (await search.BasicGetAsync(DeadName(queue), autoAck: false, ct).ConfigureAwait(false) is { } got)
        {
            var m = Read(got);
            if (m.Id != id)
                continue; // left unacknowledged: closing the channel puts it back
            var now = Now;
            await PublishDueOrWaitingAsync(search, queue, m with { Attempts = 0, EnqueuedAt = now, NotBefore = Math.Max(now, notBefore.ToUnixTimeMilliseconds()), LastError = null },
                now, ct).ConfigureAwait(false);
            await search.BasicAckAsync(got.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
            found = true;
            break;
        }

        await search.CloseAsync(ct).ConfigureAwait(false);
        return found;
    }

    public async Task<bool> DeleteDeadLetterAsync(string queue, string id, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        return await TakeOneAsync(queue, DeadName(queue), id, (_, _) => Task.CompletedTask, ct).ConfigureAwait(false);
    }

    public async Task<bool> CancelAsync(string queue, string id, string reason, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var store = _rabbit.Control
                    ?? throw new NotSupportedException("Cancelling on RabbitMQ needs RabbitMqWorkQueueOptions.Control, a store all workers share.");
        await store.MarkCancelledAsync(queue, id, reason, ct).ConfigureAwait(false);
        // A job this instance holds is dropped now; other holders drop it when they next renew or settle its lease.
        await LockedAsync(async channel =>
        {
            foreach (var (token, lease) in _leases.Where(l => l.Value.Queue == queue && l.Value.Job.Id == id).ToList())
            {
                _leases.Remove(token);
                await SettleKeyAsync(lease, c => Circuits.Settled(c, token), ct).ConfigureAwait(false);
                if (ReferenceEquals(lease.Channel, channel))
                    await channel.BasicAckAsync(lease.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
            }

            return true;
        }, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RunNowAsync(string queue, string id, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        foreach (var wait in await WaitQueuesAsync(queue, ct).ConfigureAwait(false))
        {
            var moved = await TakeOneAsync(queue, wait, id, async (channel, m) =>
            {
                var now = Now;
                await PublishAsync(channel, Name(queue), m with { NotBefore = now }, extra: null, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (moved)
                return true;
        }

        return false;
    }

    public async Task<IReadOnlyList<PendingJob>> PendingAsync(string queue, int max = 100, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);
        await LockedAsync(async channel =>
        {
            await DeclareAsync(channel, queue, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        var now = Now;
        var pending = new List<PendingJob>();
        PendingJob Row(Message m, PendingState state, long? until)
            => new(queue, m.Id, m.Tenant, m.Key, m.Source, m.Headers, state, m.Attempts, At(m.EnqueuedAt), until is { } u ? At(u) : null, m.LastError,
                Encoding.UTF8.GetByteCount(m.Body), m.ConcurrencyKey, RunTime(m.MaxRunMs));

        // Waiting jobs: take each, publish a copy (so the attempt count is not touched by a redelivery), then acknowledge it.
        await using (var main = await connection.CreateChannelAsync(Confirmed, ct).ConfigureAwait(false))
        {
            var count = (await main.QueueDeclarePassiveAsync(Name(queue), ct).ConfigureAwait(false)).MessageCount;
            for (var i = 0; i < count && pending.Count < max; i++)
            {
                if (await main.BasicGetAsync(Name(queue), autoAck: false, ct).ConfigureAwait(false) is not { } got)
                    break;
                var m = Read(got, countReturned: false);
                if (!await IsCancelledAsync(queue, m.Id, ct).ConfigureAwait(false))
                {
                    pending.Add(Row(m, m.NotBefore > now ? PendingState.Delayed : PendingState.Waiting, m.NotBefore > now ? m.NotBefore : null));
                    await PublishAsync(main, Name(queue), m with { Attempts = Read(got).Attempts }, extra: null, ct).ConfigureAwait(false);
                }

                await main.BasicAckAsync(got.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
            }

            await main.CloseAsync(ct).ConfigureAwait(false);
        }

        // Delayed jobs: wait queues have no consumers, so taking and returning their messages changes nothing that matters.
        foreach (var wait in await WaitQueuesAsync(queue, ct).ConfigureAwait(false))
        {
            if (pending.Count >= max)
                break;
            await using var peek = await connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);
            while (pending.Count < max && await peek.BasicGetAsync(wait, autoAck: false, ct).ConfigureAwait(false) is { } got)
            {
                var m = Read(got, countReturned: false);
                if (!await IsCancelledAsync(queue, m.Id, ct).ConfigureAwait(false))
                    pending.Add(Row(m, PendingState.Delayed, m.NotBefore));
            }

            await peek.CloseAsync(ct).ConfigureAwait(false);
        }

        return pending.OrderBy(p => p.EnqueuedAt).ToList();
    }

    /// <summary>Running counts and circuits come from the shared store; waiting counts are not known without reading the queue (0).</summary>
    public async Task<IReadOnlyList<ConcurrencyKeyState>> ConcurrencyKeysAsync(string queue, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        if (_rabbit.Control is not { } control)
            return [];
        var now = Now;
        return (await control.KeysAsync(queue, ct).ConfigureAwait(false)).Select(k =>
        {
            var state = Circuits.State(k.Circuit, now, _options);
            return new ConcurrencyKeyState(queue, k.Key, k.Running, 0, _options.LimitFor(k.Key), state,
                state == CircuitState.Open ? At(k.Circuit!.OpenUntilMs) : null, k.Circuit?.Failures ?? 0, _options.RateFor(k.Key), k.Starts);
        }).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_sweeper is not null)
            await _sweeper.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        // Closing the channel hands every unfinished lease back to the broker.
        if (_channel is not null)
            await _channel.DisposeAsync().ConfigureAwait(false);
        if (_ownsConnection && _connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _gate.Dispose();
    }

    private static readonly CreateChannelOptions Confirmed = new(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private TimeSpan? RunTime(long? ms) => ms is { } m ? TimeSpan.FromMilliseconds(m) : _options.DefaultMaxRunTime;

    private async Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct)
        => _rabbit.Control is { } store && await store.IsCancelledAsync(queue, id, ct).ConfigureAwait(false);

    /// <summary>Frees the lease's concurrency slot and updates its key's circuit.</summary>
    private async Task SettleKeyAsync(Lease lease, Func<CircuitRecord?, CircuitRecord?> circuit, CancellationToken ct)
    {
        if (lease.Job.ConcurrencyKey is not { } key || _rabbit.Control is not { } control)
            return;
        await control.ReleaseSlotAsync(lease.Queue, key, lease.Token, ct).ConfigureAwait(false);
        await control.UpdateCircuitAsync(lease.Queue, key, circuit, ct).ConfigureAwait(false);
    }

    /// <summary>The wait queues that exist for <paramref name="queue"/> (any worker may have made them).</summary>
    private async Task<List<string>> WaitQueuesAsync(string queue, CancellationToken ct)
    {
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);
        var found = new List<string>();
        foreach (var seconds in WaitTiersSeconds)
        {
            // A passive declare of a missing queue closes the channel, so each probe gets its own.
            await using var probe = await connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);
            try
            {
                await probe.QueueDeclarePassiveAsync(WaitName(queue, seconds), ct).ConfigureAwait(false);
                found.Add(WaitName(queue, seconds));
                await probe.CloseAsync(ct).ConfigureAwait(false);
            }
            catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
            }
        }

        return found;
    }

    /// <summary>
    /// Finds the message with <paramref name="id"/> in <paramref name="from"/>, runs <paramref name="act"/> on it (publishing on the
    /// same confirmed channel) and acknowledges it; the others go back when the channel closes.
    /// </summary>
    private async Task<bool> TakeOneAsync(string queue, string from, string id, Func<IChannel, Message, Task> act, CancellationToken ct)
    {
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);
        await LockedAsync(async channel =>
        {
            await DeclareAsync(channel, queue, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        await using var search = await connection.CreateChannelAsync(Confirmed, ct).ConfigureAwait(false);
        var found = false;
        while (await search.BasicGetAsync(from, autoAck: false, ct).ConfigureAwait(false) is { } got)
        {
            var m = Read(got, countReturned: false);
            if (m.Id != id)
                continue;
            await act(search, m).ConfigureAwait(false);
            await search.BasicAckAsync(got.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
            found = true;
            break;
        }

        await search.CloseAsync(ct).ConfigureAwait(false);
        return found;
    }

    private static DateTimeOffset At(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

    private string Name(string queue) => _rabbit.QueuePrefix + queue;

    private string DeadName(string queue) => Name(queue) + ".dead";

    private string WaitName(string queue, long seconds) => Name(queue) + ".wait." + seconds;

    private async Task<bool> WithLeaseAsync(LeasedJob job, Func<IChannel, Lease, Task> act, CancellationToken ct, bool keepLease = false)
    {
        ArgumentNullException.ThrowIfNull(job);
        return await LockedAsync(async channel =>
        {
            await ReturnExpiredAsync(channel, ct).ConfigureAwait(false);
            if (!_leases.TryGetValue(job.LeaseToken, out var lease) || lease.Job.Id != job.Id || !ReferenceEquals(lease.Channel, channel))
                return false;
            if (await IsCancelledAsync(lease.Queue, job.Id, ct).ConfigureAwait(false))
            {
                // Cancelled while this worker held it: drop it, and tell the worker it lost the job.
                _leases.Remove(job.LeaseToken);
                await channel.BasicAckAsync(lease.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
                await SettleKeyAsync(lease, c => Circuits.Settled(c, lease.Token), ct).ConfigureAwait(false);
                return false;
            }
            await act(channel, lease).ConfigureAwait(false);
            if (!keepLease)
                _leases.Remove(job.LeaseToken);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Gives back jobs whose lease ran out: a copy with the attempt counted goes back, then the delivery is acknowledged.</summary>
    private async Task ReturnExpiredAsync(IChannel channel, CancellationToken ct)
    {
        var now = Now;
        foreach (var (token, lease) in _leases.Where(l => l.Value.Until <= now).ToList())
        {
            _leases.Remove(token);
            await SettleKeyAsync(lease, c => Circuits.Settled(c, token), ct).ConfigureAwait(false);
            if (!ReferenceEquals(lease.Channel, channel))
                continue; // that channel is gone, and the broker already gave its deliveries back
            if (!await IsCancelledAsync(lease.Queue, lease.Job.Id, ct).ConfigureAwait(false))
                await PublishAsync(channel, Name(lease.Queue), lease.Job with { NotBefore = now }, extra: null, ct).ConfigureAwait(false);
            await channel.BasicAckAsync(lease.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
        }
    }

    private async Task PublishDueOrWaitingAsync(IChannel channel, string queue, Message message, long now, CancellationToken ct)
    {
        var wait = message.NotBefore - now;
        if (wait <= 0)
        {
            await PublishAsync(channel, Name(queue), message, extra: null, ct).ConfigureAwait(false);
            return;
        }

        // The longest tier that does not overshoot; under a second waits a second (late, never early).
        var seconds = WaitTiersSeconds.LastOrDefault(t => t * 1000 <= wait, WaitTiersSeconds[0]);
        await DeclareWaitAsync(channel, queue, seconds, ct).ConfigureAwait(false);
        await PublishAsync(channel, WaitName(queue, seconds), message, extra: null, ct).ConfigureAwait(false);
    }

    private Task PublishDeadAsync(IChannel channel, string queue, Message message, string reason, long now, CancellationToken ct)
        => PublishAsync(channel, DeadName(queue), message, new Dictionary<string, object?> { [Prefix + "reason"] = reason, [Prefix + "dead-at"] = now }, ct);

    private static async Task PublishAsync(IChannel channel, string routingKey, Message m, Dictionary<string, object?>? extra, CancellationToken ct)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [Prefix + "tenant"] = m.Tenant,
            [Prefix + "attempts"] = m.Attempts,
            [Prefix + "enqueued"] = m.EnqueuedAt,
            [Prefix + "not-before"] = m.NotBefore,
        };
        if (m.Key is not null) headers[Prefix + "key"] = m.Key;
        if (m.LastError is not null) headers[Prefix + "last-error"] = m.LastError;
        if (m.Source is not null) headers[Prefix + "source"] = m.Source;
        if (m.ConcurrencyKey is not null) headers[Prefix + "conc"] = m.ConcurrencyKey;
        if (m.MaxRunMs is not null) headers[Prefix + "max-run"] = m.MaxRunMs.Value;
        foreach (var (name, value) in m.Headers) headers[Prefix + "h-" + name] = value;
        foreach (var (name, value) in extra ?? []) headers[name] = value;
        var properties = new BasicProperties { MessageId = m.Id, Persistent = true, ContentType = "application/octet-stream", Headers = headers };
        // mandatory: an unroutable message fails the publish instead of vanishing; with confirms on, the await is the broker's yes.
        await channel.BasicPublishAsync(exchange: "", routingKey: routingKey, mandatory: true, basicProperties: properties,
            body: Encoding.UTF8.GetBytes(m.Body), cancellationToken: ct).ConfigureAwait(false);
    }

    private static Message Read(BasicGetResult got, bool countReturned = true)
    {
        var h = got.BasicProperties.Headers;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _) in h ?? new Dictionary<string, object?>())
            if (name.StartsWith(Prefix + "h-", StringComparison.Ordinal))
                headers[name[(Prefix.Length + 2)..]] = StringHeader(h, name[Prefix.Length..])!;
        // Returned by the broker after a crash (the worker's channel closed): our count misses those deliveries. Quorum queues
        // count them in x-delivery-count; without it, at least this one.
        var returned = !countReturned ? 0 : LongHeaderRaw(h, "x-delivery-count") is { } delivered ? (int)delivered : got.Redelivered ? 1 : 0;
        return new Message(
            got.BasicProperties.MessageId ?? throw new InvalidDataException("A work queue message has no message id."),
            StringHeader(h, "tenant") ?? throw new InvalidDataException("A work queue message has no tenant."),
            Encoding.UTF8.GetString(got.Body.Span),
            StringHeader(h, "key"),
            headers,
            (int)(LongHeader(h, "attempts") ?? 0) + returned,
            LongHeader(h, "enqueued") ?? 0,
            LongHeader(h, "not-before") ?? 0,
            StringHeader(h, "last-error"),
            StringHeader(h, "source"),
            StringHeader(h, "conc"),
            LongHeader(h, "max-run"));
    }

    private static string? StringHeader(IDictionary<string, object?>? headers, string name)
        => headers?.TryGetValue(Prefix + name, out var value) == true
            ? value switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string s => s, null => null, _ => value.ToString() }
            : null;

    private static long? LongHeaderRaw(IDictionary<string, object?>? headers, string name)
        => headers?.TryGetValue(name, out var value) == true ? value switch { long l => l, int i => i, _ => null } : null;

    private static long? LongHeader(IDictionary<string, object?>? headers, string name)
        => headers?.TryGetValue(Prefix + name, out var value) == true
            ? value switch { long l => l, int i => i, byte[] bytes => long.Parse(Encoding.UTF8.GetString(bytes), System.Globalization.CultureInfo.InvariantCulture), _ => null }
            : null;

    private async Task DeclareAsync(IChannel channel, string queue, CancellationToken ct)
    {
        if (_declared.Contains(Name(queue)))
            return;
        var quorum = new Dictionary<string, object?> { ["x-queue-type"] = "quorum" };
        // Quorum queues drop a message returned more than 20 times (RabbitMQ 4 default) unless told otherwise. Our own attempt limit
        // (counted from x-delivery-count) catches a crash loop first; the broker's limit is a backstop that dead-letters, never drops.
        var main = new Dictionary<string, object?>(quorum)
        {
            ["x-delivery-limit"] = Math.Max(_options.MaxAttempts * 2, 10),
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = DeadName(queue),
            ["x-dead-letter-strategy"] = "at-least-once",
            ["x-overflow"] = "reject-publish",
        };
        await DeclareQueueAsync(channel, Name(queue), main, ct).ConfigureAwait(false);
        // Dead letters expire after the retention period; the broker drops them (unlike the other queues, nothing is audited). Listing
        // them takes and returns them, which must never count towards a limit: none here.
        var dead = new Dictionary<string, object?>(quorum)
        {
            ["x-message-ttl"] = (long)_options.DeadLetterRetentionOrDefault.TotalMilliseconds,
            ["x-delivery-limit"] = -1,
        };
        await DeclareQueueAsync(channel, DeadName(queue), dead, ct).ConfigureAwait(false);
        _declared.Add(Name(queue)); // only once both exist as this version needs them
    }

    /// <summary>
    /// Declares a durable queue. A queue that already exists with other arguments (one declared by an earlier version of this
    /// package, whose arguments have since changed) makes RabbitMQ close the channel with PRECONDITION_FAILED; that becomes an
    /// error saying what to do, rather than a channel that fails again on every call.
    /// </summary>
    private static async Task DeclareQueueAsync(IChannel channel, string name, IDictionary<string, object?> arguments, CancellationToken ct)
    {
        try
        {
            await channel.QueueDeclareAsync(name, durable: true, exclusive: false, autoDelete: false, arguments: arguments, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 406)
        {
            throw new InvalidOperationException(
                $"RabbitMQ queue '{name}' already exists with arguments this version does not declare ({ex.ShutdownReason.ReplyText}). " +
                "RabbitMQ cannot change a durable queue's arguments in place. Migrate it: stop the workers, move its messages to a queue " +
                "declared by this version (for example with a shovel, or by setting RabbitMqWorkQueueOptions.QueuePrefix to a new prefix " +
                "and draining the old queue), then delete the old one. See docs/design/messaging-and-queues.md, 'Upgrading RabbitMQ queues'.", ex);
        }
    }

    private async Task DeclareWaitAsync(IChannel channel, string queue, long seconds, CancellationToken ct)
    {
        if (_declared.Contains(WaitName(queue, seconds)))
            return;
        // Every message here lives the same time, then dead-letters back to the work queue. At-least-once dead-lettering keeps
        // a message until the work queue confirms it (it needs reject-publish overflow).
        var arguments = new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-message-ttl"] = seconds * 1000,
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = Name(queue),
            ["x-dead-letter-strategy"] = "at-least-once",
            ["x-overflow"] = "reject-publish",
            ["x-delivery-limit"] = -1, // listing and run-now take and return messages here
        };
        await DeclareQueueAsync(channel, WaitName(queue, seconds), arguments, ct).ConfigureAwait(false);
        _declared.Add(WaitName(queue, seconds));
    }

    private async Task<T> LockedAsync<T>(Func<IChannel, Task<T>> act, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_channel is not { IsOpen: true })
            {
                // A new channel: deliveries on the old one went back to the broker, so its leases are void.
                _leases.Clear();
                _declared.Clear();
                var connection = await ConnectionAsync(ct).ConfigureAwait(false);
                if (_channel is not null)
                    await _channel.DisposeAsync().ConfigureAwait(false);
                _channel = await connection.CreateChannelAsync(Confirmed, ct).ConfigureAwait(false);
                _sweeper ??= Task.Run(SweepAsync);
            }

            return await act(_channel).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IConnection> ConnectionAsync(CancellationToken ct)
    {
        if (_connection is not null)
            return _connection;
        return _connection = await _factory!.CreateConnectionAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Gives back expired leases even when this worker is idle, so other workers get the jobs.</summary>
    private async Task SweepAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stopping.Token).ConfigureAwait(false);
                if (_leases.Count > 0)
                    await LockedAsync(async channel =>
                    {
                        await ReturnExpiredAsync(channel, _stopping.Token).ConfigureAwait(false);
                        return true;
                    }, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (!_stopping.IsCancellationRequested)
            {
                // A broken channel is replaced on the next call; the sweep tries again in a second.
            }
        }
    }

    private sealed record Message(
        string Id, string Tenant, string Body, string? Key, IReadOnlyDictionary<string, string> Headers, int Attempts, long EnqueuedAt, long NotBefore,
        string? LastError, string? Source, string? ConcurrencyKey = null, long? MaxRunMs = null);

    private sealed class Lease(string queue, Message job, ulong deliveryTag, IChannel channel, long leasedAt, long until, string token)
    {
        public string Token { get; } = token;
        public string Queue { get; } = queue;
        public Message Job { get; } = job;
        public ulong DeliveryTag { get; } = deliveryTag;
        public IChannel Channel { get; } = channel;
        public long LeasedAt { get; } = leasedAt;
        public long Until { get; set; } = until;
    }
}

/// <param name="QueuePrefix">Put before every RabbitMQ queue name. Default <c>ot.</c>.</param>
/// <param name="MaxLeaseTotal">The longest a job may stay leased, extensions included. Keep it below the broker's
/// <c>consumer_timeout</c> (30 minutes by default). Default 25 minutes.</param>
/// <param name="Control">Cancelled job ids, concurrency slots and circuits; every worker of a queue must share it. Without it, jobs
/// cannot be cancelled and jobs with a concurrency key are refused.</param>
public sealed record RabbitMqWorkQueueOptions(string QueuePrefix = "ot.", TimeSpan? MaxLeaseTotal = null, IQueueControl? Control = null)
{
    public TimeSpan MaxLeaseTotalOrDefault => MaxLeaseTotal ?? TimeSpan.FromMinutes(25);
}
