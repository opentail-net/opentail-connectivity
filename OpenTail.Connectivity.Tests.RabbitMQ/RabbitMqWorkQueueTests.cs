using OpenTail.Connectivity.Queues;
using OpenTail.Connectivity.Queues.RabbitMQ;
using Xunit;

namespace OpenTail.Connectivity.Tests.RabbitMQ;

/// <summary>
/// The RabbitMQ queue against a real broker, in real time (a few seconds per check). Needs OTAIL_QUEUE_RABBITMQ, an AMQP URI such
/// as <c>amqp://guest:guest@localhost:5672/</c>; skipped otherwise. Each check uses queues under a prefix of its own.
/// </summary>
public sealed class RabbitMqWorkQueueTests
{
    private static readonly string? Uri = Environment.GetEnvironmentVariable("OTAIL_QUEUE_RABBITMQ");

    private static WorkQueueOpener NewStorage()
    {
        var prefix = $"ot-test-{Guid.NewGuid():N}.";
        var control = new InMemoryQueueControl(); // shared by every worker opened on this storage, as production needs
        return (time, options) => new RabbitMqWorkQueue(new Uri(Uri!), options, new RabbitMqWorkQueueOptions(QueuePrefix: prefix, Control: control), time);
    }

    private static IReadOnlyList<WorkQueueConformanceCheck> Checks()
        => WorkQueueConformance.Checks(NewStorage, realTime: true, capabilities: RabbitMqWorkQueue.CapabilitiesWithControl);

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var check in Checks())
            data.Add(check.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Conformance(string check)
    {
        Assert.SkipWhen(Uri is null, "OTAIL_QUEUE_RABBITMQ is not set");
        var c = Checks().Single(x => x.Name == check);
        Assert.SkipUnless(c.AppliesTo, "RabbitMQ does not declare the capability this check needs");
        await c.RunAsync();
    }

    [Fact]
    public async Task A_queue_declared_with_other_arguments_gives_an_error_that_says_how_to_migrate()
    {
        Assert.SkipWhen(Uri is null, "OTAIL_QUEUE_RABBITMQ is not set");
        var prefix = $"ot-test-{Guid.NewGuid():N}.";
        // An earlier version's declaration: a quorum queue without the arguments this version sets.
        var factory = new global::RabbitMQ.Client.ConnectionFactory { Uri = new Uri(Uri!) };
        await using (var connection = await factory.CreateConnectionAsync(TestContext.Current.CancellationToken))
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken))
            await channel.QueueDeclareAsync(prefix + "work", durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: TestContext.Current.CancellationToken);

        await using var queue = new RabbitMqWorkQueue(new Uri(Uri!), new WorkQueueOptions(), new RabbitMqWorkQueueOptions(QueuePrefix: prefix));
        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.LeaseAsync("work", 1, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        Assert.Contains("Migrate it", first.Message);
        // Still refused on the next call, rather than carrying on as if the queue had been declared.
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.LeaseAsync("work", 1, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_lease_longer_than_the_consumer_timeout_allows_is_refused()
    {
        await using var queue = new RabbitMqWorkQueue(new Uri("amqp://localhost/"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queue.LeaseAsync("work", 1, TimeSpan.FromHours(1), TestContext.Current.CancellationToken));
    }
}
