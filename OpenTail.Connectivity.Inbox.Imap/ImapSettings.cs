namespace OpenTail.Connectivity.Inbox.Imap;

public sealed record ImapSettings(
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Password)
{
    public static ImapSettings Create(string host, int? port, bool? useSsl, string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return new ImapSettings(
            Host: host,
            Port: port ?? 993,
            UseSsl: useSsl ?? true,
            Username: username,
            Password: password);
    }
}
