namespace OpenTail.Connectivity.Files.Inbox;

public sealed class CredentialRedactor
{
    private readonly IReadOnlyList<string> _secrets;

    public CredentialRedactor(params string?[] secrets)
    {
        _secrets = secrets
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }

    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        foreach (var secret in _secrets)
            input = input.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return input;
    }

    public static CredentialRedactor FromEnv(params string[] envVarNames)
    {
        var secrets = envVarNames
            .Select(Environment.GetEnvironmentVariable)
            .ToArray();
        return new CredentialRedactor(secrets);
    }
}
