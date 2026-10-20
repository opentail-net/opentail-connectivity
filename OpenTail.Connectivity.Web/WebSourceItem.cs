namespace OpenTail.Connectivity.Web;

public static class WebSourceItemFactory
{
    public static SourceItem Create(string url, string title, string tenantContext, IReadOnlyDictionary<string, string>? metadata = null) =>
        new(Id: url,
            DisplayName: title,
            Extension: "html",
            SizeBytes: 0,
            Modified: DateTimeOffset.UtcNow,
            TenantContext: tenantContext,
            Metadata: metadata);
}
