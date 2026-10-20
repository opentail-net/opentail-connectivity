namespace OpenTail.Connectivity.Files.Contracts;

public sealed class EmailSendPolicyException : InvalidOperationException
{
    public string PolicyViolation { get; }

    public EmailSendPolicyException(string violation)
        : base($"Email send blocked by policy: {violation}")
    {
        PolicyViolation = violation;
    }
}
