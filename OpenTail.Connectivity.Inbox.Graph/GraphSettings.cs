namespace OpenTail.Connectivity.Inbox.Graph;

public sealed record GraphSettings(
    string TenantId,
    string ClientId,
    string ClientSecret,
    string UserEmail)
{
    /// <summary>Leaves out the client secret, so logging the settings does not log credentials.</summary>
    public override string ToString() => $"GraphSettings {{ TenantId = {TenantId}, ClientId = {ClientId}, UserEmail = {UserEmail} }}";

    public static GraphSettings Create(string tenantId, string clientId, string clientSecret, string userEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        return new GraphSettings(tenantId, clientId, clientSecret, userEmail);
    }
}
