namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class CredentialRedactorTests
{
    [Fact]
    public void Redact_ReplacesKnownSecret()
    {
        var r = new CredentialRedactor("super-secret-password");
        var result = r.Redact("Failed to connect: super-secret-password was invalid");
        Assert.Contains("[REDACTED]", result);
        Assert.DoesNotContain("super-secret-password", result);
    }

    [Fact]
    public void Redact_HandlesMultipleSecrets()
    {
        var r = new CredentialRedactor("token123", "clientsecret456");
        var result = r.Redact("token=token123 secret=clientsecret456 failed");
        Assert.Contains("[REDACTED]", result);
        Assert.DoesNotContain("token123", result);
        Assert.DoesNotContain("clientsecret456", result);
    }

    [Fact]
    public void Redact_IgnoresNullAndEmptySecrets()
    {
        var r = new CredentialRedactor(null, "", "   ", "realtoken");
        var result = r.Redact("error: realtoken not accepted");
        Assert.DoesNotContain("realtoken", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Redact_ReturnsOriginalWhenNoSecretPresent()
    {
        var r = new CredentialRedactor("notpresent");
        const string input = "Connection refused: server unavailable";
        Assert.Equal(input, r.Redact(input));
    }

    [Fact]
    public void Redact_HandlesEmptyInput()
    {
        var r = new CredentialRedactor("secret");
        Assert.Equal(string.Empty, r.Redact(string.Empty));
    }

    [Fact]
    public void Redact_ReplacesAllOccurrences()
    {
        var r = new CredentialRedactor("pass");
        var result = r.Redact("pass pass pass");
        Assert.Equal("[REDACTED] [REDACTED] [REDACTED]", result);
    }

    [Fact]
    public void FromEnv_PicksUpEnvironmentVariables()
    {
        var envVar = $"TEST_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envVar, "my-secret-value");
        try
        {
            var r = CredentialRedactor.FromEnv(envVar);
            var result = r.Redact("error: my-secret-value was rejected");
            Assert.Contains("[REDACTED]", result);
            Assert.DoesNotContain("my-secret-value", result);
        }
        finally { Environment.SetEnvironmentVariable(envVar, null); }
    }

    [Fact]
    public void FromEnv_IgnoresMissingVariables()
    {
        var r = CredentialRedactor.FromEnv($"NONEXISTENT_VAR_{Guid.NewGuid():N}");
        const string input = "some error message";
        Assert.Equal(input, r.Redact(input));
    }
}
