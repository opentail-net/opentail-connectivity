namespace OpenTail.Connectivity.Inbox.Imap;

public sealed record SmtpSettings(
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Password)
{
    public static SmtpSettings Create(
        string host, int? port, bool? useSsl, string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return new SmtpSettings(
            Host: host,
            Port: port ?? 587,
            UseSsl: useSsl ?? false,
            Username: username,
            Password: password);
    }
}
