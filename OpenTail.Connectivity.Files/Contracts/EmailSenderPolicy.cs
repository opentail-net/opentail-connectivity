namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>
/// Configuration-only object that controls what email sends are permitted.
/// Must be supplied by the application host (appsettings / DI) — workflows and
/// models receive only <see cref="IEmailSender"/> and cannot access or modify this.
/// All properties default to the most restrictive posture (sending disabled).
/// </summary>
public sealed class EmailSenderPolicy
{
    /// <summary>A policy instance with all sending disabled — use as a safe default.</summary>
    public static EmailSenderPolicy Disabled { get; } = new();

    /// <summary>
    /// Master switch. All sends are blocked unless this is true.
    /// Must be explicitly set to true by the operator; no code path sets it automatically.
    /// </summary>
    public bool SendingEnabled { get; init; } = false;

    /// <summary>
    /// When non-empty, only recipients whose domain matches an entry in this list are allowed.
    /// Checked against every To and Cc address before sending.
    /// </summary>
    public IReadOnlyList<string> AllowedRecipientDomains { get; init; } = [];

    /// <summary>
    /// Specific email addresses always allowed, regardless of the domain list.
    /// Useful for internal aliases that sit on a different domain.
    /// </summary>
    public IReadOnlyList<string> AllowedRecipientAddresses { get; init; } = [];

    /// <summary>
    /// Hard cap on outbound sends per hour. 0 = no hourly cap (master switch still applies).
    /// </summary>
    public int MaxEmailsPerHour { get; init; } = 0;

    /// <summary>
    /// Hard cap on outbound sends per calendar day (UTC). 0 = no daily cap.
    /// </summary>
    public int MaxEmailsPerDay { get; init; } = 0;

    /// <summary>
    /// When true, the sender may only reply to existing threads (InReplyToMessageId must be set).
    /// Prevents unsolicited outbound email to new recipients.
    /// </summary>
    public bool ReplyOnlyMode { get; init; } = false;

    /// <summary>
    /// When true, <see cref="IEmailSender.SendAsync"/> always throws
    /// <see cref="EmailSendPolicyException"/> with code "approval-required".
    /// The caller is expected to route sends through a human approval gate instead.
    /// Default is true to prevent any accidental automated sending.
    /// </summary>
    public bool RequireHumanApproval { get; init; } = true;
}
