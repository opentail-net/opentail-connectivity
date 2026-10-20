namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class ImapSettingsTests
{
    [Fact]
    public void Create_DefaultsTo993AndSsl()
    {
        var s = ImapSettings.Create("imap.example.com", null, null, "user@example.com", "pass");
        Assert.Equal(993, s.Port);
        Assert.True(s.UseSsl);
    }

    [Fact]
    public void Create_RespectsExplicitPort()
    {
        var s = ImapSettings.Create("imap.example.com", 143, false, "user@example.com", "pass");
        Assert.Equal(143, s.Port);
        Assert.False(s.UseSsl);
    }

    [Fact]
    public void Create_PreservesCredentials()
    {
        var s = ImapSettings.Create("imap.example.com", null, null, "alice@corp.com", "secret");
        Assert.Equal("imap.example.com", s.Host);
        Assert.Equal("alice@corp.com", s.Username);
        Assert.Equal("secret", s.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ThrowsForBlankHost(string host)
    {
        Assert.Throws<ArgumentException>(() =>
            ImapSettings.Create(host, null, null, "user", "pass"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ThrowsForBlankUsername(string username)
    {
        Assert.Throws<ArgumentException>(() =>
            ImapSettings.Create("imap.example.com", null, null, username, "pass"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ThrowsForBlankPassword(string password)
    {
        Assert.Throws<ArgumentException>(() =>
            ImapSettings.Create("imap.example.com", null, null, "user", password));
    }
}
