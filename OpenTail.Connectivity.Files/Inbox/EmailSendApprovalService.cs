namespace OpenTail.Connectivity.Files.Inbox;

/// <summary>
/// Approves or rejects queued sends. An approval claims the record (<see cref="PendingSendStatus.Pending"/> to
/// <see cref="PendingSendStatus.Sending"/>) before anything is sent, so two approvals of one record send it once. At approval it
/// checks the policy again and takes a rate slot. Build it with <see cref="PolicyEnforcingEmailSender.CreateApprovalService"/>,
/// which shares the queueing sender's store, raw sender, policy and limiter; a separately built limiter splits the cap.
/// </summary>
/// <remarks>
/// At most once from this store's point of view; exactly once is not promised. A failure the provider may have accepted
/// (anything but a policy refusal or a cancellation before sending) leaves the record <see cref="PendingSendStatus.Unknown"/>,
/// never re-sent automatically: a person checks the mailbox and calls <see cref="RequeueAsync"/> if it did not go out.
/// The claim is only as atomic as the store's <see cref="IPendingSendStore.TryTransitionAsync"/>: <see cref="InMemoryPendingSendStore"/>
/// serves one process. Approvals from several processes need a store whose transition is atomic across them, such as a database.
/// </remarks>
public sealed class EmailSendApprovalService : IEmailSendApprovalService
{
    private readonly IPendingSendStore _store;
    private readonly IEmailSender _rawSender;
    private readonly EmailSenderPolicy _policy;
    private readonly IEmailRateLimiter? _rateLimiter;

    /// <param name="policy">Checked again at approval (sending enabled, reply-only, recipients).</param>
    /// <param name="rateLimiter">Takes a slot at approval. Required when the policy sets an hourly or daily cap, and must be the
    /// one the queueing sender uses.</param>
    public EmailSendApprovalService(IPendingSendStore store, IEmailSender rawSender, EmailSenderPolicy policy, IEmailRateLimiter? rateLimiter = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _rawSender = rawSender ?? throw new ArgumentNullException(nameof(rawSender));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (rateLimiter is null && (policy.MaxEmailsPerHour > 0 || policy.MaxEmailsPerDay > 0))
            throw new ArgumentException("The policy caps sends, so approvals need the queueing sender's rate limiter.", nameof(rateLimiter));
        _rateLimiter = rateLimiter;
    }

    public Task<PendingSendRecord?> GetAsync(string id, CancellationToken ct = default)
        => _store.GetAsync(id, ct);

    public Task<IReadOnlyList<PendingSendRecord>> GetPendingAsync(CancellationToken ct = default)
        => _store.GetPendingAsync(ct);

    public async Task<string> ApproveAsync(string id, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Pending send '{id}' not found.");

        if (!await _store.TryTransitionAsync(id, PendingSendStatus.Pending, PendingSendStatus.Sending, null, ct).ConfigureAwait(false))
            throw AlreadyHandled(id, (await _store.GetAsync(id, ct).ConfigureAwait(false))?.Status ?? record.Status);

        // From here the record is claimed: every status write uses CancellationToken.None so a cancelled caller cannot strand it.
        try
        {
            if (!_policy.SendingEnabled)
                throw new EmailSendPolicyException("sending is disabled (SendingEnabled = false)");
            PolicyEnforcingEmailSender.EnforcePolicyExceptApproval(_policy, record.Email);
        }
        catch (EmailSendPolicyException ex)
        {
            await _store.UpdateStatusAsync(id, PendingSendStatus.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        EmailRateReservation? reservation;
        try
        {
            reservation = _rateLimiter?.Reserve();
        }
        catch (EmailSendPolicyException)
        {
            // Over the cap now is not a reason to drop the send: it can be approved again later.
            await _store.UpdateStatusAsync(id, PendingSendStatus.Pending, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        string sentId;
        var sendStarted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            sendStarted = true;
            sentId = await _rawSender.SendAsync(record.Email, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!sendStarted)
        {
            Release(reservation);
            await _store.UpdateStatusAsync(id, PendingSendStatus.Pending, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (EmailSendPolicyException ex)
        {
            Release(reservation);
            await _store.UpdateStatusAsync(id, PendingSendStatus.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await _store.UpdateStatusAsync(id, PendingSendStatus.Unknown, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        try
        {
            await _store.UpdateStatusAsync(id, PendingSendStatus.Approved, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Pending send '{id}' WAS sent (provider id '{sentId}'), but its status could not be saved. Do not approve it again.", ex);
        }

        return sentId;
    }

    public async Task RejectAsync(string id, string? reason = null, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Pending send '{id}' not found.");

        if (!await _store.TryTransitionAsync(id, PendingSendStatus.Pending, PendingSendStatus.Rejected, reason, ct).ConfigureAwait(false))
            throw AlreadyHandled(id, (await _store.GetAsync(id, ct).ConfigureAwait(false))?.Status ?? record.Status);
    }

    public async Task RequeueAsync(string id, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Pending send '{id}' not found.");

        if (record.Status is not (PendingSendStatus.Unknown or PendingSendStatus.Failed)
            || !await _store.TryTransitionAsync(id, record.Status, PendingSendStatus.Pending, "requeued", ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Pending send '{id}' is '{record.Status}'; only unknown or failed sends can be requeued.");
    }

    private void Release(EmailRateReservation? reservation)
    {
        if (reservation is not null)
            _rateLimiter!.Release(reservation);
    }

    private static InvalidOperationException AlreadyHandled(string id, string status)
        => new($"Pending send '{id}' is already '{status}'.");
}
