namespace OpenTail.Connectivity.Files.Models;

/// <summary>Standard <see cref="SourceItem.Metadata"/> keys, as they appear in the document's metadata. Every key is optional.</summary>
public static class SourceKeys
{
    /// <summary>A link a person can open.</summary>
    public const string Url = "source.url";

    /// <summary>The folder, site, space, project, bucket or channel the item belongs to.</summary>
    public const string Container = "source.container";

    /// <summary>The owner or author, as the source identifies them.</summary>
    public const string Owner = "source.owner";

    /// <summary><c>private</c>, <c>restricted</c>, <c>organisation</c>, <c>public</c> or <c>unknown</c>.</summary>
    public const string AclVisibility = "source.acl.visibility";

    /// <summary>A JSON array of the user, group or role ids allowed to read the item, in the source's own ids.</summary>
    public const string AclPrincipals = "source.acl.principals";

    /// <summary><c>true</c> only when the connector read the permissions from the source.</summary>
    public const string AclCaptured = "source.acl.captured";

    /// <summary><c>true</c> when the principal list was cut at its cap; the item is then marked restricted.</summary>
    public const string AclTruncated = "source.acl.truncated";
}

/// <summary>
/// Permissions as metadata. <b>Absence is not permission:</b> unless <see cref="SourceKeys.AclCaptured"/> is <c>true</c>, a
/// consumer must treat the item as restricted. Permissions describe the source when the item was fetched; re-ingest to refresh.
/// </summary>
public static class SourceAcl
{
    public const int DefaultMaxPrincipals = 500;

    /// <summary>Metadata for permissions the connector read: visibility and, when given, the principals (capped).</summary>
    public static Dictionary<string, string> Captured(string visibility, IEnumerable<string>? principals = null, int maxPrincipals = DefaultMaxPrincipals)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SourceKeys.AclCaptured] = "true",
            [SourceKeys.AclVisibility] = visibility,
        };
        if (principals is null) return meta;

        var list = principals.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).Take(maxPrincipals + 1).ToList();
        if (list.Count > maxPrincipals)
        {
            // Cut, never silently: someone allowed is missing, so the item must not look as open as it is.
            list.RemoveAt(list.Count - 1);
            meta[SourceKeys.AclTruncated] = "true";
            meta[SourceKeys.AclVisibility] = "restricted";
        }
        meta[SourceKeys.AclPrincipals] = JsonSerializer.Serialize(list);
        return meta;
    }

    /// <summary>Metadata for a connector that did not read permissions.</summary>
    public static Dictionary<string, string> NotCaptured() => new(StringComparer.OrdinalIgnoreCase)
    {
        [SourceKeys.AclCaptured] = "false",
        [SourceKeys.AclVisibility] = "unknown",
    };

    /// <summary>Whether the document's permissions were read from the source. False for anything missing or unclear.</summary>
    public static bool IsCaptured(IReadOnlyDictionary<string, string> documentMetadata) =>
        documentMetadata.TryGetValue(SourceKeys.AclCaptured, out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
}
