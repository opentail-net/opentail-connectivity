namespace OpenTail.Connectivity.Social.Shared;

/// <param name="TenantContext">Set on every item, as other connectors take it in their constructor. Empty gives a MISSING_TENANT warning per document.</param>
public sealed record SocialConnectorOptions(
    int MaxItems = 100,
    DateTimeOffset? Since = null,
    IReadOnlyList<string>? Keywords = null,
    IReadOnlyList<string>? Hashtags = null,
    int RequestDelayMs = 250,
    string MetaApiVersion = MetaGraph.DefaultVersion,
    int? MaxCacheItems = null,
    string TenantContext = "");

/// <summary>The Graph API version used when none is configured. Meta retires a version about two years after release (v25.0:
/// released 2026-02-18, expires 2028-07-29) and sends calls to a retired one to the oldest version still served, so the version
/// in use would drift without notice; keep this current. The fields read have not been checked against a live account yet.</summary>
public static class MetaGraph
{
    public const string DefaultVersion = "v25.0";
}
