namespace OpenTail.Connectivity.Files.Validation;

public static class DocumentValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(NormalisedDocument document, int minBodyLength = 50)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(document.Body))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "MISSING_BODY", "Document body is empty.", document.SourceRef));

        if (string.IsNullOrWhiteSpace(document.Title))
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "MISSING_TITLE", "Document title is missing.", document.SourceRef));

        if (string.IsNullOrWhiteSpace(document.TenantContext))
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "MISSING_TENANT", "Tenant context is not set.", document.SourceRef));

        if (string.IsNullOrWhiteSpace(document.SourceRef))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "MISSING_SOURCE_REF", "Source reference is missing.", null));

        if (!string.IsNullOrWhiteSpace(document.Body) && document.Body.Length < minBodyLength)
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "SHORT_CONTENT",
                $"Document body is very short ({document.Body.Length} chars).", document.SourceRef));

        if (string.IsNullOrWhiteSpace(document.ContentHash))
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "MISSING_HASH", "Content hash is missing.", document.SourceRef));

        return issues;
    }
}
