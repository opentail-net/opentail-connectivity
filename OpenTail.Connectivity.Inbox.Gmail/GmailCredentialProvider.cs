using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;

namespace OpenTail.Connectivity.Inbox.Gmail;

/// <summary>
/// Single shared OAuth2 credential for all Gmail adapters bound to one account.
/// Eliminates duplicate per-adapter token refresh and enables file-backed persistence so
/// the token survives restarts without a new browser consent flow.
/// </summary>
public sealed class GmailCredentialProvider
{
    private readonly GmailSettings _settings;
    private readonly IDataStore? _dataStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GoogleAuthorizationCodeFlow? _flow;
    private UserCredential? _credential;

    /// <param name="scopes">
    /// The scopes the flow asks for. Default <see cref="GmailScopes.Full"/>. What a refresh token can do is fixed by the scopes it
    /// was granted at consent; asking for fewer here does not narrow an existing token. Get the token with the narrowest set
    /// (<see cref="GmailScopes.ReadOnly"/> for an inbox reader) and pass the same set here.
    /// </param>
    public GmailCredentialProvider(GmailSettings settings, IDataStore? dataStore = null, IReadOnlyList<string>? scopes = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dataStore = dataStore;
        Scopes = scopes is { Count: > 0 } ? [.. scopes] : GmailScopes.Full;
    }

    /// <summary>The scopes this provider's flow asks for.</summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>
    /// Creates a provider that persists the token to <paramref name="storePath"/> via
    /// <see cref="FileDataStore"/> so it survives process restarts.
    /// </summary>
    public static GmailCredentialProvider WithFileStore(GmailSettings settings, string storePath, IReadOnlyList<string>? scopes = null)
        => new(settings, new FileDataStore(storePath, fullPath: true), scopes);

    /// <summary>
    /// Returns a valid credential, refreshing the access token on first call (and after expiry).
    /// Thread-safe; all Gmail adapters sharing this provider serialise through one gate.
    /// </summary>
    public async Task<ICredential> GetCredentialAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_credential is not null)
                return _credential;

            _flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId  = _settings.ClientId,
                    ClientSecret = _settings.ClientSecret
                },
                Scopes    = Scopes,
                DataStore = _dataStore
            });

            _credential = new UserCredential(_flow, _settings.UserEmail, new TokenResponse
            {
                RefreshToken = _settings.RefreshToken
            });

            await _credential.RefreshTokenAsync(ct);
            return _credential;
        }
        finally
        {
            _gate.Release();
        }
    }
}
