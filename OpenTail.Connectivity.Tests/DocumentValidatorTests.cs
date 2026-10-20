namespace OpenTail.Connectivity.Tests;

public sealed class DocumentValidatorTests
{
    private static NormalisedDocument ValidDoc(
        string body = "This body is long enough to pass the short-content check.",
        string title = "Title",
        string tenant = "tenant",
        string sourceRef = "/path",
        string hash = "abc") =>
        new(Id: Guid.NewGuid().ToString(), Title: title, Body: body, ContentHash: hash,
            SourceRef: sourceRef, SourceType: "file", TenantContext: tenant,
            SourceModified: DateTimeOffset.UtcNow, IngestedAt: DateTimeOffset.UtcNow,
            Metadata: new Dictionary<string, string>());

    [Fact]
    public void Valid_document_produces_no_issues()
    {
        var issues = DocumentValidator.Validate(ValidDoc());
        Assert.Empty(issues);
    }

    [Fact]
    public void Empty_body_produces_MISSING_BODY_error()
    {
        var issues = DocumentValidator.Validate(ValidDoc(body: ""));
        Assert.Contains(issues, i => i.Code == "MISSING_BODY" && i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Whitespace_body_produces_MISSING_BODY_error()
    {
        var issues = DocumentValidator.Validate(ValidDoc(body: "   "));
        Assert.Contains(issues, i => i.Code == "MISSING_BODY");
    }

    [Fact]
    public void Empty_title_produces_MISSING_TITLE_warning()
    {
        var issues = DocumentValidator.Validate(ValidDoc(title: ""));
        Assert.Contains(issues, i => i.Code == "MISSING_TITLE" && i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void Empty_tenant_produces_MISSING_TENANT_warning()
    {
        var issues = DocumentValidator.Validate(ValidDoc(tenant: ""));
        Assert.Contains(issues, i => i.Code == "MISSING_TENANT");
    }

    [Fact]
    public void Empty_source_ref_produces_MISSING_SOURCE_REF_error()
    {
        var issues = DocumentValidator.Validate(ValidDoc(sourceRef: ""));
        Assert.Contains(issues, i => i.Code == "MISSING_SOURCE_REF" && i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Short_body_produces_SHORT_CONTENT_warning()
    {
        var issues = DocumentValidator.Validate(ValidDoc(body: "Short."));
        Assert.Contains(issues, i => i.Code == "SHORT_CONTENT" && i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void Body_exactly_50_chars_does_not_trigger_SHORT_CONTENT()
    {
        var body = new string('x', 50);
        var issues = DocumentValidator.Validate(ValidDoc(body: body));
        Assert.DoesNotContain(issues, i => i.Code == "SHORT_CONTENT");
    }

    [Fact]
    public void Empty_hash_produces_MISSING_HASH_warning()
    {
        var issues = DocumentValidator.Validate(ValidDoc(hash: ""));
        Assert.Contains(issues, i => i.Code == "MISSING_HASH" && i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void Multiple_issues_all_reported()
    {
        var doc = ValidDoc(body: "", title: "", tenant: "", sourceRef: "");
        var issues = DocumentValidator.Validate(doc);
        Assert.True(issues.Count >= 4);
    }
}
