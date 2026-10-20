namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class EmailSendApprovalServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otail-approval-" + Guid.NewGuid().ToString("N"));

    public EmailSendApprovalServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Two_concurrent_approvals_send_once()
    {
        var store = await StoreWith("a");
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new FakeSender((_, _) => release.Task);
        var service = new EmailSendApprovalService(store, sender, Open);

        var first = Observe(service.ApproveAsync("a"));
        var second = Observe(service.ApproveAsync("a"));
        release.SetResult("provider-1");
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, sender.SendCount);
        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r is InvalidOperationException);
        Assert.Equal(PendingSendStatus.Approved, (await store.GetAsync("a"))!.Status);
    }

    [Fact]
    public async Task A_claim_that_cannot_be_saved_sends_nothing()
    {
        var sender = new FakeSender((_, _) => Task.FromResult("provider-1"));
        var service = new EmailSendApprovalService(new UnsaveableClaims(await StoreWith("a")), sender, Open);

        await Assert.ThrowsAsync<IOException>(() => service.ApproveAsync("a"));

        Assert.Equal(0, sender.SendCount);
    }

    [Fact]
    public async Task A_failed_save_throws_and_is_not_kept()
    {
        var blocked = Path.Combine(_dir, "is-a-directory");
        Directory.CreateDirectory(blocked);
        var store = new InMemoryPendingSendStore(blocked);

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(Record("a")));

        Assert.Null(await store.GetAsync("a"));
    }

    [Fact]
    public async Task A_record_left_sending_loads_as_unknown_and_is_not_approvable()
    {
        var path = Path.Combine(_dir, "pending.json");
        var store = new InMemoryPendingSendStore(path);
        await store.SaveAsync(Record("a") with { Status = PendingSendStatus.Sending });

        var reloaded = new InMemoryPendingSendStore(path);
        var service = new EmailSendApprovalService(reloaded, new FakeSender((_, _) => Task.FromResult("x")), Open);

        Assert.Equal(PendingSendStatus.Unknown, (await reloaded.GetAsync("a"))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync("a"));
    }

    [Fact]
    public async Task A_corrupt_file_is_moved_aside_not_silently_emptied()
    {
        var path = Path.Combine(_dir, "pending.json");
        await File.WriteAllTextAsync(path, "{ not json");

        Assert.Throws<InvalidDataException>(() => new InMemoryPendingSendStore(path));

        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "pending.json.corrupt-*"));
    }

    [Fact]
    public async Task Cancellation_after_the_send_still_records_it_as_sent()
    {
        var store = await StoreWith("a");
        using var cancellation = new CancellationTokenSource();
        var sender = new FakeSender((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult("provider-1");
        });

        Assert.Equal("provider-1", await new EmailSendApprovalService(store, sender, Open).ApproveAsync("a", cancellation.Token));

        Assert.Equal(PendingSendStatus.Approved, (await store.GetAsync("a"))!.Status);
    }

    [Fact]
    public async Task An_ambiguous_failure_is_unknown_until_a_person_requeues_it()
    {
        var store = await StoreWith("a");
        var fail = true;
        var sender = new FakeSender((_, _) => fail ? Task.FromException<string>(new TimeoutException("no reply")) : Task.FromResult("provider-1"));
        var service = new EmailSendApprovalService(store, sender, Open);

        await Assert.ThrowsAsync<TimeoutException>(() => service.ApproveAsync("a"));
        Assert.Equal(PendingSendStatus.Unknown, (await store.GetAsync("a"))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync("a"));

        fail = false;
        await service.RequeueAsync("a");
        Assert.Equal("provider-1", await service.ApproveAsync("a"));
        Assert.Equal(2, sender.SendCount);
    }

    [Fact]
    public async Task Approvals_take_a_slot_from_the_shared_limiter_and_stay_pending_when_over_it()
    {
        var store = await StoreWith("a", "b");
        var policy = new EmailSenderPolicy { SendingEnabled = true, RequireHumanApproval = true, MaxEmailsPerHour = 1 };
        var sender = new FakeSender((_, _) => Task.FromResult("provider"));
        var service = new EmailSendApprovalService(store, sender, policy, new InProcessEmailRateLimiter(policy));

        await service.ApproveAsync("a");
        await Assert.ThrowsAsync<EmailSendPolicyException>(() => service.ApproveAsync("b"));

        Assert.Equal(1, sender.SendCount);
        Assert.Equal(PendingSendStatus.Pending, (await store.GetAsync("b"))!.Status);
    }

    [Fact]
    public async Task Recipient_policy_is_checked_again_at_approval()
    {
        var store = await StoreWith("a");
        var policy = new EmailSenderPolicy { SendingEnabled = true, RequireHumanApproval = true, AllowedRecipientDomains = ["other.example"] };
        var sender = new FakeSender((_, _) => Task.FromResult("provider"));

        await Assert.ThrowsAsync<EmailSendPolicyException>(() => new EmailSendApprovalService(store, sender, policy).ApproveAsync("a"));

        Assert.Equal(0, sender.SendCount);
        Assert.Equal(PendingSendStatus.Failed, (await store.GetAsync("a"))!.Status);
    }

    [Fact]
    public async Task Direct_and_approved_sends_share_one_cap_when_built_from_the_sender()
    {
        var policy = new EmailSenderPolicy { SendingEnabled = true, RequireHumanApproval = true, MaxEmailsPerHour = 1 };
        var store = await StoreWith("queued");
        var raw = new FakeSender((_, _) => Task.FromResult("provider"));
        var sender = new PolicyEnforcingEmailSender(raw, policy, store);

        await sender.CreateApprovalService().ApproveAsync("queued");

        // The approval used the hour's one slot, so the same sender refuses to queue another.
        await Assert.ThrowsAsync<EmailSendPolicyException>(() => sender.SendAsync(new OutboundEmail(["user@example.com"], "s", "b")));
        Assert.Equal(1, raw.SendCount);
    }

    [Fact]
    public async Task One_sender_refuses_an_approval_after_a_direct_send_used_the_cap()
    {
        var store = await StoreWith("queued");
        var raw = new FakeSender((_, _) => Task.FromResult("provider"));
        var direct = new PolicyEnforcingEmailSender(raw, new EmailSenderPolicy { SendingEnabled = true, RequireHumanApproval = false, MaxEmailsPerHour = 1 }, store);
        var approvals = direct.CreateApprovalService();

        await direct.SendAsync(new OutboundEmail(["user@example.com"], "s", "b"));

        await Assert.ThrowsAsync<EmailSendPolicyException>(() => approvals.ApproveAsync("queued"));
        Assert.Equal(1, raw.SendCount);
        Assert.Equal(PendingSendStatus.Pending, (await store.GetAsync("queued"))!.Status);
    }

    [Fact]
    public void A_capped_policy_without_a_limiter_is_refused()
    {
        var policy = new EmailSenderPolicy { SendingEnabled = true, MaxEmailsPerDay = 10 };

        Assert.Throws<ArgumentException>(() => new EmailSendApprovalService(new InMemoryPendingSendStore(), new FakeSender((_, _) => Task.FromResult("x")), policy));
        Assert.Throws<InvalidOperationException>(() => new PolicyEnforcingEmailSender(new FakeSender((_, _) => Task.FromResult("x")), policy).CreateApprovalService());
    }

    private static EmailSenderPolicy Open { get; } = new() { SendingEnabled = true, RequireHumanApproval = true };

    private static async Task<InMemoryPendingSendStore> StoreWith(params string[] ids)
    {
        var store = new InMemoryPendingSendStore();
        foreach (var id in ids)
            await store.SaveAsync(Record(id));
        return store;
    }

    private static PendingSendRecord Record(string id)
        => new(id, new OutboundEmail(["user@example.com"], "subject", "body"), DateTimeOffset.UtcNow);

    private static async Task<Exception?> Observe(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class UnsaveableClaims(IPendingSendStore inner) : IPendingSendStore
    {
        public Task SaveAsync(PendingSendRecord record, CancellationToken ct = default) => inner.SaveAsync(record, ct);
        public Task<PendingSendRecord?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<IReadOnlyList<PendingSendRecord>> GetPendingAsync(CancellationToken ct = default) => inner.GetPendingAsync(ct);
        public Task UpdateStatusAsync(string id, string status, string? note = null, CancellationToken ct = default) => inner.UpdateStatusAsync(id, status, note, ct);
        public Task<bool> TryTransitionAsync(string id, string from, string to, string? note = null, CancellationToken ct = default)
            => Task.FromException<bool>(new IOException("disk full"));
    }

    private sealed class FakeSender(Func<OutboundEmail, CancellationToken, Task<string>> send) : IEmailSender
    {
        private int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);

        public Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _sendCount);
            return send(email, ct);
        }

        public Task DeleteAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkReadAsync(string messageId, bool isRead, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveAsync(string messageId, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
    }
}
