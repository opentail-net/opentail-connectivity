using System.Globalization;
using StackExchange.Redis;

namespace OpenTail.Connectivity.Queues.Redis;

/// <param name="Prefix">Put before every key, so applications sharing a Redis do not share controls. Default <c>ot:</c>.</param>
/// <param name="CancellationRetention">How long cancellations are kept. Default 30 days, as the other stores.</param>
/// <param name="CircuitRetries">Compare-and-set attempts for one circuit update under contention before it fails. Default 50.</param>
public sealed record RedisQueueControlOptions(string Prefix = "ot:", TimeSpan? CancellationRetention = null, int CircuitRetries = 50)
{
    internal TimeSpan RetentionOrDefault => CancellationRetention ?? TimeSpan.FromDays(30);
}

/// <summary>
/// Queue control in Redis or Valkey, shared by every worker of a queue on any machine (for a broker's queue, RabbitMQ, that cannot
/// keep concurrency slots, circuits, rate starts or cancellations itself).
/// </summary>
/// <remarks>
/// <para>Every change is one Lua script, atomic on the server, and slots and rate windows use <b>the server's clock</b>
/// (<c>TIME</c>), so workers whose clocks disagree still agree on when a slot lapses. A circuit update reads the circuit, applies the
/// caller's (pure) change in .NET, and writes only if the circuit is still exactly what was read, retrying a bounded number of times:
/// comparing the value itself needs no version counter.</para>
/// <para>All of a queue's keys share a hash tag (<c>{queue}</c>), so the scripts are valid on Redis Cluster; this is designed for
/// but tested only against standalone Redis and Valkey. Slot and start keys expire on their own; a registry of keys, used by
/// <see cref="KeysAsync"/>, is pruned by the scripts that empty a key and by <see cref="KeysAsync"/> itself.</para>
/// <para>When Redis cannot be reached every call throws; workers then back off and lease nothing.</para>
/// </remarks>
public sealed class RedisQueueControl(IConnectionMultiplexer redis, RedisQueueControlOptions? options = null) : IQueueControl
{
    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    private readonly RedisQueueControlOptions _options = Checked(options ?? new RedisQueueControlOptions());

    private static RedisQueueControlOptions Checked(RedisQueueControlOptions o)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(o.Prefix, nameof(o.Prefix));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(o.RetentionOrDefault, TimeSpan.Zero, nameof(o.CancellationRetention));
        ArgumentOutOfRangeException.ThrowIfLessThan(o.CircuitRetries, 1, nameof(o.CircuitRetries));
        return o;
    }

    private IDatabase Db => _redis.GetDatabase();

    // -- keys ----------------------------------------------------------------------------------------

    /// <summary>Escapes what would break the hash tag or the key layout, so any queue or key name is safe.</summary>
    internal static string Encode(string s) => s.Replace("%", "%25", StringComparison.Ordinal).Replace("{", "%7B", StringComparison.Ordinal)
        .Replace("}", "%7D", StringComparison.Ordinal).Replace(":", "%3A", StringComparison.Ordinal);

    private string QueuePart(string queue) => _options.Prefix + "{" + Encode(queue) + "}:";

    /// <summary>KEYS[1] slots, KEYS[2] starts, KEYS[3] circuit, KEYS[4] the queue's registry: every script gets all four.</summary>
    private RedisKey[] KeysFor(string queue, string key)
    {
        var q = QueuePart(queue);
        var k = Encode(key);
        return [q + "slots:" + k, q + "starts:" + k, q + "circuit:" + k, q + "keys"];
    }

    private RedisKey CancelKey(string queue, string id) => QueuePart(queue) + "cancel:" + Encode(id);

    // -- scripts -------------------------------------------------------------------------------------

    private const string Common = """
        local function now_ms()
          local t = redis.call('TIME')
          return tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        end
        local function prune()
          if redis.call('EXISTS', KEYS[1]) == 0 and redis.call('EXISTS', KEYS[2]) == 0 and redis.call('EXISTS', KEYS[3]) == 0 then
            redis.call('SREM', KEYS[4], ARGV[1])
          end
        end
        local function expire_slots()
          local top = redis.call('ZRANGE', KEYS[1], -1, -1, 'WITHSCORES')
          if #top == 2 then redis.call('PEXPIREAT', KEYS[1], math.floor(tonumber(top[2]))) end
        end
        """;

    // ARGV: name, limit, token, lease ms
    private const string TakeSlot = Common + """

        local now = now_ms()
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) then return 0 end
        redis.call('ZADD', KEYS[1], now + tonumber(ARGV[4]), ARGV[3])
        expire_slots()
        redis.call('SADD', KEYS[4], ARGV[1])
        return 1
        """;

    // ARGV: name, token, lease ms. A lapsed slot is removed first, so it can never be renewed.
    private const string RenewSlot = Common + """

        local now = now_ms()
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        if not redis.call('ZSCORE', KEYS[1], ARGV[2]) then
          prune()
          return 0
        end
        redis.call('ZADD', KEYS[1], 'XX', now + tonumber(ARGV[3]), ARGV[2])
        expire_slots()
        return 1
        """;

    // ARGV: name, token
    private const string ReleaseSlot = Common + """

        redis.call('ZREM', KEYS[1], ARGV[2])
        prune()
        return 1
        """;

    // ARGV: name, count, window ms, unique member suffix
    private const string TakeStart = Common + """

        local now = now_ms()
        local window = tonumber(ARGV[3])
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now - window)
        if redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[2]) then return 0 end
        redis.call('ZADD', KEYS[2], now, now .. ':' .. ARGV[4])
        redis.call('PEXPIRE', KEYS[2], math.max(window, 3600000))
        redis.call('SADD', KEYS[4], ARGV[1])
        return 1
        """;

    // ARGV: name, expected (the circuit as read, '' when absent), next ('' to remove)
    private const string SwapCircuit = Common + """

        local current = redis.call('GET', KEYS[3])
        if current == false then current = '' end
        if current ~= ARGV[2] then return 0 end
        if ARGV[3] == '' then
          redis.call('DEL', KEYS[3])
          prune()
        else
          redis.call('SET', KEYS[3], ARGV[3])
          redis.call('SADD', KEYS[4], ARGV[1])
        end
        return 1
        """;

    // ARGV: name. Live slots, starts within the last hour, the circuit; prunes the registry when nothing is left.
    private const string ReadKey = Common + """

        local now = now_ms()
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        local running = redis.call('ZCARD', KEYS[1])
        local starts = redis.call('ZCOUNT', KEYS[2], '(' .. (now - 3600000), '+inf')
        local circuit = redis.call('GET', KEYS[3])
        prune()
        if circuit == false then circuit = '' end
        return { running, starts, circuit }
        """;

    private async Task<long> RunAsync(string script, string queue, string key, params RedisValue[] args)
        => (long)await Db.ScriptEvaluateAsync(script, KeysFor(queue, key), [key, .. args]).ConfigureAwait(false);

    private static string Ms(TimeSpan t) => ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    // -- IQueueControl -------------------------------------------------------------------------------

    public async Task MarkCancelledAsync(string queue, string id, string reason, CancellationToken ct = default)
        => await Db.StringSetAsync(CancelKey(queue, id), reason, _options.RetentionOrDefault).ConfigureAwait(false);

    public Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct = default)
        => Db.KeyExistsAsync(CancelKey(queue, id));

    public async Task<bool> TryTakeSlotAsync(string queue, string key, int limit, string token, TimeSpan lease, CancellationToken ct = default)
        => await RunAsync(TakeSlot, queue, key, limit, token, Ms(lease)).ConfigureAwait(false) == 1;

    public async Task<bool> RenewSlotAsync(string queue, string key, string token, TimeSpan lease, CancellationToken ct = default)
        => await RunAsync(RenewSlot, queue, key, token, Ms(lease)).ConfigureAwait(false) == 1;

    public async Task ReleaseSlotAsync(string queue, string key, string token, CancellationToken ct = default)
        => await RunAsync(ReleaseSlot, queue, key, token).ConfigureAwait(false);

    public async Task<bool> TryTakeStartAsync(string queue, string key, JobRateLimit rate, CancellationToken ct = default)
        => await RunAsync(TakeStart, queue, key, rate.Count, Ms(rate.Per), Guid.NewGuid().ToString("N")).ConfigureAwait(false) == 1;

    public async Task<CircuitRecord?> UpdateCircuitAsync(string queue, string key, Func<CircuitRecord?, CircuitRecord?> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var circuitKey = KeysFor(queue, key)[2];
        for (var attempt = 0; attempt < _options.CircuitRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var read = (string?)await Db.StringGetAsync(circuitKey).ConfigureAwait(false) ?? "";
            var next = change(Parse(read));
            if (await RunAsync(SwapCircuit, queue, key, read, Serialize(next)).ConfigureAwait(false) == 1)
                return next;
            await Task.Delay(Random.Shared.Next(1, 20), ct).ConfigureAwait(false); // another worker wrote in between: read again
        }
        throw new InvalidOperationException($"The circuit of '{key}' kept changing under {_options.CircuitRetries} attempts to update it.");
    }

    public async Task<IReadOnlyList<(string Key, int Running, CircuitRecord? Circuit, int Starts)>> KeysAsync(string queue, CancellationToken ct = default)
    {
        var result = new List<(string, int, CircuitRecord?, int)>();
        await foreach (var member in Db.SetScanAsync(QueuePart(queue) + "keys", pageSize: 250).WithCancellation(ct).ConfigureAwait(false))
        {
            var key = (string)member!;
            var row = (RedisResult[])(await Db.ScriptEvaluateAsync(ReadKey, KeysFor(queue, key), [key]).ConfigureAwait(false))!;
            var running = (int)row[0];
            var starts = (int)row[1];
            var circuit = Parse((string?)row[2] ?? "");
            if (running > 0 || starts > 0 || circuit is not null)
                result.Add((key, running, circuit, starts));
        }
        return result;
    }

    // -- circuit serialisation: deterministic, so the compare-and-set can compare strings ------------

    internal static string Serialize(CircuitRecord? c) => c is null ? "" : string.Join('|',
        c.Failures.ToString(CultureInfo.InvariantCulture), c.OpenUntilMs.ToString(CultureInfo.InvariantCulture),
        c.NextCooldownMs.ToString(CultureInfo.InvariantCulture), c.ProbeUntilMs.ToString(CultureInfo.InvariantCulture),
        c.ProbeToken is null ? "n" : "s" + c.ProbeToken);

    internal static CircuitRecord? Parse(string s)
    {
        if (s.Length == 0) return null;
        var p = s.Split('|', 5);
        return new CircuitRecord(int.Parse(p[0], CultureInfo.InvariantCulture), long.Parse(p[1], CultureInfo.InvariantCulture),
            long.Parse(p[2], CultureInfo.InvariantCulture), p[4] == "n" ? null : p[4][1..], long.Parse(p[3], CultureInfo.InvariantCulture));
    }
}
