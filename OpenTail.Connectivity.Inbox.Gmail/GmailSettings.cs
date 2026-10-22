namespace OpenTail.Connectivity.Inbox.Gmail;

public sealed record GmailSettings(
    string ClientId,
    string ClientSecret,
    string RefreshToken,
    string UserEmail)
{
    /// <summary>Leaves out the client secret and refresh token, so logging the settings does not log credentials.</summary>
    public override string ToString() => $"GmailSettings {{ ClientId = {ClientId}, UserEmail = {UserEmail} }}";

    public static GmailSettings Create(string clientId, string clientSecret, string refreshToken, string userEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        return new GmailSettings(clientId, clientSecret, refreshToken, userEmail);
    }
}
