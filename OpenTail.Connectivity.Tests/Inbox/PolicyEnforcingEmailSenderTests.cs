namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class PolicyEnforcingEmailSenderTests
{
    [Fact]
    public async Task Concurrent_sends_reserve_rate_slots_before_entering_inner_sender()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new FakeSender((_, _) => release.Task);
        var sender = new PolicyEnforcingEmailSender(inner, Policy());
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => ObserveAsync(sender.SendAsync(Email)))
            .ToArray();

        release.SetResult("sent");
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, inner.SendCount);
        Assert.Equal(9, results.Count(result => result is EmailSendPolicyException));
        Assert.Equal(1, results.Count(result => result is null));
    }

    [Fact]
    public async Task Ambiguous_inner_failure_keeps_the_rate_slot_taken()
    {
        var inner = new FakeSender((_, _) => Task.FromException<string>(new InvalidOperationException("ambiguous")));
        var sender = new PolicyEnforcingEmailSender(inner, Policy());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Email));

        await Assert.ThrowsAsync<EmailSendPolicyException>(() => sender.SendAsync(Email));
        Assert.Equal(1, inner.SendCount);
    }

    [Fact]
    public void Rate_limit_allows_a_send_after_the_hour_window_rolls_over()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        var limiter = new InProcessEmailRateLimiter(Policy(), time);

        limiter.Reserve();
        time.Advance(TimeSpan.FromHours(1));

        var reservation = limiter.Reserve();
        Assert.NotNull(reservation);
    }

    [Fact]
    public async Task Cancellation_before_inner_send_releases_the_reserved_slot()
    {
        var inner = new FakeSender((_, _) => Task.FromResult("sent"));
        var sender = new PolicyEnforcingEmailSender(inner, Policy());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Email, cancellation.Token));

        Assert.Equal("sent", await sender.SendAsync(Email));
        Assert.Equal(1, inner.SendCount);
    }

    private static EmailSenderPolicy Policy() => new()
    {
        SendingEnabled = true,
        RequireHumanApproval = false,
        MaxEmailsPerHour = 1,
    };

    private static OutboundEmail Email { get; } = new(["user@example.com"], "subject", "body");

    private static async Task<Exception?> ObserveAsync(Task<string> send)
    {
        try
        {
            await send;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class FakeSender(Func<OutboundEmail, CancellationToken, Task<string>> send) : IEmailSender, IMailboxManager
    {
        private int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);

        public Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _sendCount);
            return send(email, ct);
        }

        public List<string> Mutations { get; } = [];
        public Task DeleteAsync(string messageId, CancellationToken ct = default) { Mutations.Add("delete " + messageId); return Task.CompletedTask; }
        public Task MarkReadAsync(string messageId, bool isRead, CancellationToken ct = default) { Mutations.Add($"read {messageId} {isRead}"); return Task.CompletedTask; }
        public Task MoveAsync(string messageId, string destinationFolder, CancellationToken ct = default) { Mutations.Add($"move {messageId} {destinationFolder}"); return Task.CompletedTask; }
    }

    [Fact]
    public void A_send_only_consumer_cannot_reach_mailbox_changes()
    {
        IEmailSender sender = new PolicyEnforcingEmailSender(new FakeSender((_, _) => Task.FromResult("id")),
            new EmailSenderPolicy { SendingEnabled = true, AllowDelete = true });

        Assert.False(sender is IMailboxManager); // the wrapper hides the provider's mailbox methods even when the policy allows them
    }

    [Fact]
    public async Task Mailbox_changes_are_denied_by_default_without_a_provider_call()
    {
        var inner = new FakeSender((_, _) => Task.FromResult("id"));
        var sender = new PolicyEnforcingMailboxManager(inner, EmailSenderPolicy.Disabled);

        await Assert.ThrowsAsync<EmailSendPolicyException>(() => sender.DeleteAsync("m1"));
        await Assert.ThrowsAsync<EmailSendPolicyException>(() => sender.MarkReadAsync("m1", true));
        await Assert.ThrowsAsync<EmailSendPolicyException>(() => sender.MoveAsync("m1", "Archive"));

        Assert.Empty(inner.Mutations);
    }

    [Fact]
    public async Task Allowed_mailbox_changes_pass_their_arguments_once()
    {
        var inner = new FakeSender((_, _) => Task.FromResult("id"));
        var sender = new PolicyEnforcingMailboxManager(inner,
            new EmailSenderPolicy { AllowDelete = true, AllowMarkRead = true, AllowMove = true });

        await sender.DeleteAsync("m1");
        await sender.MarkReadAsync("m2", false);
        await sender.MoveAsync("m3", "Archive");

        Assert.Equal(["delete m1", "read m2 False", "move m3 Archive"], inner.Mutations);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
