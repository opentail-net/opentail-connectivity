namespace OpenTail.Connectivity.Social.Shared;

/// <param name="TenantContext">Set on every item, as other connectors take it in their constructor. Empty gives a MISSING_TENANT warning per document.</param>
public sealed record SocialConnectorOptions(
    int MaxItems = 100,
    DateTimeOffset? Since = null,
    IReadOnlyList<string>? Keywords = null,
    IReadOnlyList<string>? Hashtags = null,
    int RequestDelayMs = 250,
    string MetaApiVersion = "v19.0",
    int? MaxCacheItems = null,
    string TenantContext = "");
