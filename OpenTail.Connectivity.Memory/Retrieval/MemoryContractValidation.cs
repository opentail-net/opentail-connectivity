// Moved from opentail's OpenTail.Core.Memory (MemoryContractValidation.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public static class MemoryContractValidation
{
    public static void Validate(MemoryRetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request.Query));

        if (request.TopK <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.TopK), "TopK must be greater than 0.");

        if (request.FromUtc is not null &&
            request.ToUtc is not null &&
            request.FromUtc > request.ToUtc)
            throw new InvalidOperationException("FromUtc must be earlier than or equal to ToUtc.");
    }

    public static void Validate(MemoryEmbeddingModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(model.Provider))
            throw new ArgumentException("Embedding provider is required.", nameof(model.Provider));

        if (string.IsNullOrWhiteSpace(model.Model))
            throw new ArgumentException("Embedding model is required.", nameof(model.Model));

        if (string.IsNullOrWhiteSpace(model.DistanceMetric))
            throw new ArgumentException("Embedding distance metric is required.", nameof(model.DistanceMetric));

        if (model.Dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(model.Dimensions), "Embedding dimensions must be greater than 0.");
    }

    public static void Validate(MemoryLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (link.Weight is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(link.Weight), "Link weight must be between 0 and 1.");
    }

    public static void Validate(MemoryFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        if (string.IsNullOrWhiteSpace(fact.Statement))
            throw new ArgumentException("Fact statement is required.", nameof(fact.Statement));

        if (fact.Confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(fact.Confidence), "Fact confidence must be between 0 and 1.");

        if (fact.ValidFromUtc is not null &&
            fact.ValidToUtc is not null &&
            fact.ValidFromUtc > fact.ValidToUtc)
            throw new InvalidOperationException("ValidFromUtc must be earlier than or equal to ValidToUtc.");

        if (fact.IsSuperseded && string.IsNullOrWhiteSpace(fact.SupersededByFactId))
            throw new InvalidOperationException("Superseded facts must include SupersededByFactId.");
    }

    public static void Validate(MemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.Id))
            throw new ArgumentException("Record id is required.", nameof(record.Id));

        if (string.IsNullOrWhiteSpace(record.Content) && string.IsNullOrWhiteSpace(record.Summary))
            throw new InvalidOperationException("Memory records must include Content or Summary.");

        if (record.BaseImportance is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(record.BaseImportance), "BaseImportance must be between 0 and 1.");

        if (record.Policy.RetentionClass == MemoryRetentionClass.Expiring && record.Policy.ExpiresAtUtc is null)
            throw new InvalidOperationException("Expiring retention requires ExpiresAtUtc.");

        if (record.DeletedAtUtc is not null && string.IsNullOrWhiteSpace(record.DeletedReason))
            throw new InvalidOperationException("Deleted records must include DeletedReason.");

        if (record.IsSuperseded && string.IsNullOrWhiteSpace(record.SupersededByRecordId))
            throw new InvalidOperationException("Superseded records must include SupersededByRecordId.");

        if (record.IsSuperseded && record.SupersededAtUtc is null)
            throw new InvalidOperationException("Superseded records must include SupersededAtUtc.");

        if (!string.IsNullOrWhiteSpace(record.ContentHash))
        {
            var hashValue = record.ContentHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? record.ContentHash[7..]
                : record.ContentHash;

            if (!hashValue.All(char.IsAsciiHexDigit))
                throw new InvalidOperationException("ContentHash must be a valid hex string.");
        }
    }
}
