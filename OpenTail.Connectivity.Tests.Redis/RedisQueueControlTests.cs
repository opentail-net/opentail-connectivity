using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OpenTail.Connectivity.Queues;
using OpenTail.Connectivity.Queues.RabbitMQ;
using OpenTail.Connectivity.Queues.Redis;
using StackExchange.Redis;
using Xunit;

namespace OpenTail.Connectivity.Tests.Redis;

/// <summary>
/// <see cref="RedisQueueControl"/> against real servers, in real time (Redis keeps its own clock). <c>OTAIL_QUEUE_REDIS</c> and
/// <c>OTAIL_QUEUE_VALKEY</c> are StackExchange.Redis configurations such as <c>localhost:6379</c>; <c>OTAIL_QUEUE_RABBITMQ</c> an
/// AMQP URI. Each test uses keys and queues under a prefix of its own.
/// </summary>
public sealed class RedisQueueControlTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(3);
    private static readonly string? RabbitUri = Environment.GetEnvironmentVariable("OTAIL_QUEUE_RABBITMQ");

    private static readonly Dictionary<string, string?> Servers = new(StringComparer.Ordinal)
    {
        ["redis"] = Environment.GetEnvironmentVariable("OTAIL_QUEUE_REDIS"),
        ["valkey"] = Environment.GetEnvironmentVariable("OTAIL_QUEUE_VALKEY"),
    };

    private static readonly ConcurrentDictionary<string, IConnectionMultiplexer> Connections = new(StringComparer.Ordinal);

    private static IConnectionMultiplexer Connect(string configuration)
        => Connections.GetOrAdd(configuration, c => ConnectionMultiplexer.Connect(c));

    private static string NewPrefix() => $"ot-test-{Guid.NewGuid():N}:";

    private static Func<QueueControlOpener> Storage(string server) => () =>
    {
        var control = new RedisQueueControl(Connect(Servers[server]!), new RedisQueueControlOptions(NewPrefix(), Retention));
        return _ => control; // a second open is a second worker on the same keys
    };

    // -- without a server ----------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("a|b|c")]
    [InlineData("")]
    public void A_circuit_survives_serialisation_exactly(string? probe)
    {
        var c = new CircuitRecord(3, 1_700_000_000_123, 30_000, probe, 1_700_000_001_000);
        Assert.Equal(c, RedisQueueControl.Parse(RedisQueueControl.Serialize(c)));
        Assert.Null(RedisQueueControl.Parse(RedisQueueControl.Serialize(null)));
    }

    [Fact]
    public void Names_cannot_break_the_hash_tag_or_the_key_layout()
    {
        Assert.Equal("a%3Ab%7Bc%7Dd%25e", RedisQueueControl.Encode("a:b{c}d%e"));
        Assert.NotEqual(RedisQueueControl.Encode("a:b"), RedisQueueControl.Encode("a%3Ab"));
    }

    // -- the contract, against Redis and Valkey -------------------------------------------------------

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var server in Servers.Keys)
            foreach (var check in QueueControlConformance.Checks(() => _ => null!, Retention, realTime: true))
                data.Add(server, check.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Server_KeepsTheContract(string server, string check)
    {
        Assert.SkipWhen(Servers[server] is null, $"OTAIL_QUEUE_{server.ToUpperInvariant()} is not set");
        await QueueControlConformance.Checks(Storage(server), Retention, realTime: true).Single(x => x.Name == check).RunAsync();
    }

    // -- the RabbitMQ queue with Redis as its shared store --------------------------------------------

    public static TheoryData<string> RabbitCases()
    {
        var data = new TheoryData<string>();
        foreach (var check in RabbitChecks())
            data.Add(check.Name);
        return data;
    }

    private static IReadOnlyList<WorkQueueConformanceCheck> RabbitChecks() => WorkQueueConformance.Checks(() =>
    {
        var prefix = NewPrefix();
        var control = new RedisQueueControl(Connect(Servers["redis"]!), new RedisQueueControlOptions(prefix));
        return (time, options) => new RabbitMqWorkQueue(new Uri(RabbitUri!), options,
            new RabbitMqWorkQueueOptions(QueuePrefix: prefix.Replace(':', '.'), Control: control), time);
    }, realTime: true, capabilities: RabbitMqWorkQueue.CapabilitiesWithControl);

    [Theory]
    [MemberData(nameof(RabbitCases))]
    public async Task RabbitMq_with_a_Redis_control_store(string check)
    {
        Assert.SkipWhen(RabbitUri is null || Servers["redis"] is null, "OTAIL_QUEUE_RABBITMQ and OTAIL_QUEUE_REDIS are needed");
        var c = RabbitChecks().Single(x => x.Name == check);
        Assert.SkipUnless(c.AppliesTo, "RabbitMQ does not declare the capability this check needs");
        await c.RunAsync();
    }

    // -- an outage ------------------------------------------------------------------------------------

    [Fact]
    public async Task While_Redis_is_unreachable_the_worker_backs_off_and_starts_nothing_then_recovers()
    {
        Assert.SkipWhen(RabbitUri is null || Servers["redis"] is null, "OTAIL_QUEUE_RABBITMQ and OTAIL_QUEUE_REDIS are needed");
        var ct = TestContext.Current.CancellationToken;
        var target = Servers["redis"]!.Split(',')[0].Split(':');
        await using var proxy = TcpProxy.Start(target[0], target.Length > 1 ? int.Parse(target[1], System.Globalization.CultureInfo.InvariantCulture) : 6379);
        using var redis = await ConnectionMultiplexer.ConnectAsync(
            $"127.0.0.1:{proxy.Port},abortConnect=false,connectTimeout=1000,syncTimeout=1000,asyncTimeout=1000,connectRetry=1");
        var prefix = NewPrefix();
        await using var queue = new RabbitMqWorkQueue(new Uri(RabbitUri!), new WorkQueueOptions(),
            new RabbitMqWorkQueueOptions(QueuePrefix: prefix.Replace(':', '.'), Control: new RedisQueueControl(redis, new RedisQueueControlOptions(prefix))));

        var started = new ConcurrentQueue<string>();
        var events = new ConcurrentQueue<WorkerEvent>();
        using var stop = new CancellationTokenSource();
        var worker = new WorkQueueWorker(queue, new WorkQueueWorkerOptions("work", new HashSet<string> { "acme" },
                IdleWait: TimeSpan.FromMilliseconds(200), RetryMaxDelay: TimeSpan.FromSeconds(2)),
            (job, _) => { started.Enqueue(job.Body); return Task.CompletedTask; }, events.Enqueue);
        var run = worker.RunAsync(stop.Token);

        await queue.EnqueueAsync(new EnqueueRequest("work", "acme", "before", ConcurrencyKey: "db:a"), ct);
        await Until(() => started.Contains("before"), TimeSpan.FromSeconds(20), "the first job did not run while Redis was up");

        proxy.Stop();
        await queue.EnqueueAsync(new EnqueueRequest("work", "acme", "during", ConcurrencyKey: "db:a"), ct);
        await Until(() => events.Any(e => e.Kind == WorkerEventKind.QueueError), TimeSpan.FromSeconds(20), "the worker reported no queue error");
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Assert.DoesNotContain("during", started); // never treated an unreachable control store as permission

        proxy.Resume();
        await Until(() => started.Contains("during"), TimeSpan.FromSeconds(60), "the job did not run after Redis came back");

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Single(started, b => b == "during"); // and only once: the message was parked, not stranded or duplicated
    }

    private static async Task Until(Func<bool> condition, TimeSpan within, string failure)
    {
        var deadline = DateTime.UtcNow + within;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail(failure);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A pass-through TCP proxy on 127.0.0.1 that can be stopped (closing every connection) and resumed on the same port.</summary>
    private sealed class TcpProxy : IAsyncDisposable
    {
        private readonly string _host;
        private readonly int _targetPort;
        private readonly ConcurrentBag<TcpClient> _clients = [];
        private TcpListener? _listener;
        private CancellationTokenSource _running = new();

        private TcpProxy(string host, int port) { _host = host; _targetPort = port; }

        public int Port { get; private set; }

        public static TcpProxy Start(string host, int port)
        {
            var proxy = new TcpProxy(host, port);
            proxy.Listen(0);
            return proxy;
        }

        private void Listen(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            var token = _running.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(token); }
                    catch (Exception) { return; }
                    var upstream = new TcpClient();
                    try { await upstream.ConnectAsync(_host, _targetPort, token); }
                    catch (Exception) { client.Dispose(); upstream.Dispose(); continue; }
                    _clients.Add(client);
                    _clients.Add(upstream);
                    _ = Pump(client.GetStream(), upstream.GetStream(), token);
                    _ = Pump(upstream.GetStream(), client.GetStream(), token);
                }
            }, CancellationToken.None);
        }

        private static async Task Pump(Stream from, Stream to, CancellationToken token)
        {
            try { await from.CopyToAsync(to, token); }
            catch (Exception) { }
        }

        public void Stop()
        {
            _running.Cancel();
            _listener?.Stop();
            while (_clients.TryTake(out var c)) c.Dispose();
        }

        public void Resume()
        {
            _running = new CancellationTokenSource();
            Listen(Port);
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            return ValueTask.CompletedTask;
        }
    }
}
