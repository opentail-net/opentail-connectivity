using System.Globalization;
using System.Text.Json;
using OpenTail.Connectivity.Files.Models;

namespace OpenTail.Connectivity.Files.Security;

/// <summary>
/// Trusted identity information supplied by the host, not by a search request. Principal IDs include the authenticated user
/// and verified group memberships.
/// </summary>
public sealed record SourceAccessContext(
    string TenantContext,
    IReadOnlySet<string> EffectivePrincipalIds,
    TimeSpan MaxAclAge);

public enum DocumentAccessStatus
{
    AllowedPrincipal,
    AllowedPublic,
    DeniedInvalidContext,
    DeniedTenantMismatch,
    DeniedAclNotCaptured,
    DeniedAclTruncated,
    DeniedAclStale,
    DeniedInvalidAcl,
    DeniedNoMatchingPrincipal,
}

public readonly record struct DocumentAccessDecision(bool Allowed, DocumentAccessStatus Status);

/// <summary>
/// A fail-closed access decision helper. Consumers must call it before returning protected metadata or content to downstream
/// processing. It does not itself enforce search-store filtering or establish the caller's identity.
/// </summary>
public static class DocumentAccessPolicy
{
    /// <summary>Explicit anonymous read grant. Do not use for "anyone with the link" permissions.</summary>
    public const string PublicPrincipal = "public:anonymous";

    public static DocumentAccessDecision Evaluate(NormalisedDocument document, SourceAccessContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.TenantContext) || context.EffectivePrincipalIds is null ||
            context.MaxAclAge <= TimeSpan.Zero)
            return Deny(DocumentAccessStatus.DeniedInvalidContext);

        if (!string.Equals(document.TenantContext, context.TenantContext, StringComparison.Ordinal))
            return Deny(DocumentAccessStatus.DeniedTenantMismatch);

        var metadata = document.Metadata;
        var capturedEntries = Find(metadata, SourceKeys.AclCaptured);
        if (capturedEntries.Length == 0)
            return Deny(DocumentAccessStatus.DeniedAclNotCaptured);
        if (capturedEntries.Length != 1 || !bool.TryParse(capturedEntries[0], out var isCaptured))
            return Deny(DocumentAccessStatus.DeniedInvalidAcl);
        if (!isCaptured)
            return Deny(DocumentAccessStatus.DeniedAclNotCaptured);

        var truncatedEntries = Find(metadata, SourceKeys.AclTruncated);
        if (truncatedEntries.Length > 1)
            return Deny(DocumentAccessStatus.DeniedInvalidAcl);
        if (truncatedEntries.Length == 1)
        {
            if (!bool.TryParse(truncatedEntries[0], out var isTruncated))
                return Deny(DocumentAccessStatus.DeniedInvalidAcl);
            if (isTruncated)
                return Deny(DocumentAccessStatus.DeniedAclTruncated);
        }

        if (!TryRead(metadata, SourceKeys.AclCapturedAt, out var capturedAtText) ||
            !DateTimeOffset.TryParse(capturedAtText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var capturedAt))
            return Deny(DocumentAccessStatus.DeniedAclStale);

        var now = DateTimeOffset.UtcNow;
        if (capturedAt > now || now - capturedAt > context.MaxAclAge)
            return Deny(DocumentAccessStatus.DeniedAclStale);

        if (!TryRead(metadata, SourceKeys.AclPrincipals, out var json) || !TryParsePrincipals(json, out var principals))
            return Deny(DocumentAccessStatus.DeniedInvalidAcl);

        foreach (var principal in principals)
        {
            if (string.Equals(principal, PublicPrincipal, StringComparison.Ordinal))
                return new DocumentAccessDecision(true, DocumentAccessStatus.AllowedPublic);

            if (context.EffectivePrincipalIds.Any(id => string.Equals(id, principal, StringComparison.Ordinal)))
                return new DocumentAccessDecision(true, DocumentAccessStatus.AllowedPrincipal);
        }

        return Deny(DocumentAccessStatus.DeniedNoMatchingPrincipal);
    }

    private static DocumentAccessDecision Deny(DocumentAccessStatus status) => new(false, status);

    private static bool TryRead(IReadOnlyDictionary<string, string>? metadata, string key, out string value)
    {
        value = string.Empty;
        if (metadata is null) return false;

        var found = false;
        foreach (var pair in metadata)
        {
            if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (found)
            {
                value = string.Empty;
                return false;
            }
            found = true;
            value = pair.Value;
        }
        return found;
    }

    private static string[] Find(IReadOnlyDictionary<string, string>? metadata, string key) =>
        metadata?.Where(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value).ToArray() ?? [];

    private static bool TryParsePrincipals(string json, out List<string> principals)
    {
        principals = [];
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var element in parsed.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
                    return false;
                principals.Add(element.GetString()!);
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
