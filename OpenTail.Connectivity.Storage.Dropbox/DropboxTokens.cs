using System.Text.Json;

namespace OpenTail.Connectivity.Storage.Dropbox;

/// <summary>Creates cached Dropbox access tokens from an OAuth refresh token.</summary>
public static class DropboxTokens
{
    private static readonly Uri TokenEndpoint = new("https://api.dropboxapi.com/oauth2/token");

    public static Func<CancellationToken, Task<string>> FromRefreshToken(
        string appKey,
        string appSecret,
        string refreshToken,
        HttpClient? http = null) =>
        FromRefreshToken(appKey, appSecret, refreshToken, http, () => DateTimeOffset.UtcNow);

    internal static Func<CancellationToken, Task<string>> FromRefreshToken(
        string appKey,
        string appSecret,
        string refreshToken,
        HttpClient? http,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(appSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentNullException.ThrowIfNull(utcNow);
        var client = http ?? new HttpClient();
        var gate = new SemaphoreSlim(1, 1);
        string? accessToken = null;
        var refreshAt = DateTimeOffset.MinValue;

        return async cancellationToken =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (accessToken is not null && utcNow() < refreshAt)
                    return accessToken;

                using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refreshToken,
                        ["client_id"] = appKey,
                        ["client_secret"] = appSecret,
                    }),
                };
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Dropbox token refresh failed with status {(int)response.StatusCode} ({response.StatusCode}).");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                accessToken = json.RootElement.GetProperty("access_token").GetString();
                if (string.IsNullOrWhiteSpace(accessToken))
                    throw new InvalidDataException("Dropbox returned an empty access token.");
                var expiresIn = json.RootElement.GetProperty("expires_in").GetInt64();
                refreshAt = utcNow().AddSeconds(expiresIn).AddMinutes(-1);
                return accessToken;
            }
            finally
            {
                gate.Release();
            }
        };
    }
}
