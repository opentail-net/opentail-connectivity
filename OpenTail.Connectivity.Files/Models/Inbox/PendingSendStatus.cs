namespace OpenTail.Connectivity.Files.Models;

/// <summary>The states of a <see cref="PendingSendRecord"/>. Only <see cref="Pending"/> can be approved or rejected.</summary>
public static class PendingSendStatus
{
    public const string Pending = "pending";

    /// <summary>Claimed by one approval and being handed to the provider. Found at startup, it becomes <see cref="Unknown"/>.</summary>
    public const string Sending = "sending";

    /// <summary>Approved and accepted by the provider.</summary>
    public const string Approved = "approved";

    /// <summary>The provider definitely did not accept it, or policy refused it at approval.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// The provider may or may not have accepted it (a timeout, a lost reply, a crash mid-send). Never re-sent automatically: a
    /// person checks the mailbox and requeues it only if it was not sent.
    /// </summary>
    public const string Unknown = "unknown";

    public const string Rejected = "rejected";
}
