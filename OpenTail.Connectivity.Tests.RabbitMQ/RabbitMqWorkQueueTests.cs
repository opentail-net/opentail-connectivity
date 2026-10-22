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
        var cancelled = new InMemoryCancelledJobs(); // shared by every worker opened on this storage, as production needs
        return (time, options) => new RabbitMqWorkQueue(new Uri(Uri!), options, new RabbitMqWorkQueueOptions(QueuePrefix: prefix, CancelledJobs: cancelled), time);
    }

    private static IReadOnlyList<WorkQueueConformanceCheck> Checks()
        => WorkQueueConformance.Checks(NewStorage, realTime: true, capabilities: RabbitMqWorkQueue.CapabilitiesWithCancellation);

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
    public async Task A_lease_longer_than_the_consumer_timeout_allows_is_refused()
    {
        await using var queue = new RabbitMqWorkQueue(new Uri("amqp://localhost/"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queue.LeaseAsync("work", 1, TimeSpan.FromHours(1), TestContext.Current.CancellationToken));
    }
}
