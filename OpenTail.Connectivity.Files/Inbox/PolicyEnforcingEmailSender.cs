namespace OpenTail.Connectivity.Files.Inbox;

/// <summary>
/// Wraps any <see cref="IEmailSender"/> and enforces <see cref="EmailSenderPolicy"/> before
/// every send. Non-send operations (delete, mark-read, move) pass through unconditionally.
/// Rate-limit state is per limiter instance. Share one <see cref="InProcessEmailRateLimiter"/> between senders in a process;
/// across processes, inject an <see cref="IEmailRateLimiter"/> backed by shared state.
/// </summary>
public sealed class PolicyEnforcingEmailSender : IEmailSender
{
    private readonly IEmailSender _inner;
    private readonly EmailSenderPolicy _policy;
    private readonly IPendingSendStore? _approvalStore;
    private readonly IEmailRateLimiter _rateLimiter;

    public PolicyEnforcingEmailSender(
        IEmailSender inner,
        EmailSenderPolicy policy,
        IPendingSendStore? approvalStore = null,
        IEmailRateLimiter? rateLimiter = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _approvalStore = approvalStore;
        _rateLimiter = rateLimiter ?? new InProcessEmailRateLimiter(policy);
    }

    public async Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        if (!_policy.SendingEnabled)
            throw new EmailSendPolicyException("sending is disabled (SendingEnabled = false)");

        if (_policy.RequireHumanApproval)
        {
            if (_approvalStore is null)
                throw new EmailSendPolicyException(
                    "RequireHumanApproval is active — sends must go through an approval gate");

            EnforcePolicyExceptApproval(_policy, email);
            _rateLimiter.EnsureWithinLimits();
            var record = new PendingSendRecord(Guid.NewGuid().ToString("N"), email, DateTimeOffset.UtcNow);
            await _approvalStore.SaveAsync(record, ct);
            return $"pending:{record.Id}";
        }

        EnforcePolicyExceptApproval(_policy, email);
        var reservation = _rateLimiter.Reserve();
        var innerSendStarted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            innerSendStarted = true;
            return await _inner.SendAsync(email, ct).ConfigureAwait(false);
        }
        catch (EmailSendPolicyException)
        {
            _rateLimiter.Release(reservation);
            throw;
        }
        catch (OperationCanceledException) when (!innerSendStarted)
        {
            _rateLimiter.Release(reservation);
            throw;
        }
    }

    /// <summary>
    /// The approval service for the sends this sender queues: the same store, raw sender, policy and rate limiter, so approved
    /// and direct sends share one cap. The production way to build one.
    /// </summary>
    public EmailSendApprovalService CreateApprovalService()
        => new(_approvalStore ?? throw new InvalidOperationException("This sender has no approval store, so it queues nothing to approve."),
            _inner, _policy, _rateLimiter);

    public Task DeleteAsync(string messageId, CancellationToken ct = default)
        => _inner.DeleteAsync(messageId, ct);

    public Task MarkReadAsync(string messageId, bool isRead, CancellationToken ct = default)
        => _inner.MarkReadAsync(messageId, isRead, ct);

    public Task MoveAsync(string messageId, string destinationFolder, CancellationToken ct = default)
        => _inner.MoveAsync(messageId, destinationFolder, ct);

    /// <summary>Reply-only and recipient rules: everything but approval and rate. The approval service checks these again at send.</summary>
    internal static void EnforcePolicyExceptApproval(EmailSenderPolicy policy, OutboundEmail email)
    {
        if (policy.ReplyOnlyMode && string.IsNullOrWhiteSpace(email.InReplyToMessageId))
            throw new EmailSendPolicyException(
                "ReplyOnlyMode is active — only replies to existing threads are permitted");

        if (policy.AllowedRecipientDomains.Count > 0 || policy.AllowedRecipientAddresses.Count > 0)
            EnforceRecipients(policy, email);
    }

    private static void EnforceRecipients(EmailSenderPolicy policy, OutboundEmail email)
    {
        var allAddresses = email.To.SelectMany(ParseAddresses);
        if (email.Cc is not null)
            allAddresses = allAddresses.Concat(email.Cc.SelectMany(ParseAddresses));

        foreach (var addr in allAddresses)
        {
            if (policy.AllowedRecipientAddresses.Contains(addr, StringComparer.OrdinalIgnoreCase))
                continue;

            var domain = addr.Contains('@') ? addr[(addr.IndexOf('@') + 1)..] : string.Empty;
            if (policy.AllowedRecipientDomains.Count > 0 &&
                policy.AllowedRecipientDomains.Any(d => domain.Equals(d, StringComparison.OrdinalIgnoreCase)))
                continue;

            throw new EmailSendPolicyException(
                $"recipient '{addr}' is not in AllowedRecipientDomains or AllowedRecipientAddresses");
        }
    }

    private static IEnumerable<string> ParseAddresses(string? addressList)
    {
        if (string.IsNullOrWhiteSpace(addressList))
            return [];

        return addressList
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(a =>
            {
                a = a.Trim();
                var lt = a.LastIndexOf('<');
                var gt = a.LastIndexOf('>');
                return lt >= 0 && gt > lt ? a[(lt + 1)..gt].Trim() : a;
            })
            .Where(a => !string.IsNullOrWhiteSpace(a));
    }
}
