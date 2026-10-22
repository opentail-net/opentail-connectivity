using OpenTail.Connectivity.Inbox.Gmail;
using OpenTail.Connectivity.Inbox.Graph;
using OpenTail.Connectivity.Inbox.Imap;
using OpenTail.Connectivity.Sql;

namespace OpenTail.Connectivity.Tests;

public sealed class SettingsRedactionTests
{
    private const string Secret = "s3cr3t-value";

    public static TheoryData<object> SettingsWithSecrets() =>
    [
        new GmailSettings("client", Secret, Secret, "me@example.com"),
        new GraphSettings("tenant", "client", Secret, "me@example.com"),
        new ImapSettings("imap.example.com", 993, true, "me", Secret),
        new SmtpSettings("smtp.example.com", 587, true, "me", Secret),
        new SqlConnectorConfig($"Data Source=x.db;Password={Secret}", "tickets", "id", "subject", ["body"]),
    ];

    [Theory]
    [MemberData(nameof(SettingsWithSecrets))]
    public void ToString_never_contains_a_secret(object settings)
    {
        var text = settings.ToString()!;

        Assert.DoesNotContain(Secret, text);
        Assert.Contains(settings.GetType().Name, text);
    }
}
