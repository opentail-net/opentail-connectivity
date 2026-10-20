namespace OpenTail.Connectivity.Files.Extraction;

public sealed class TransformationsTextExtractor : ITextExtractor
{
    private readonly TT.TextExtractor _inner = new();

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        var result = _inner.GetTextWithMetadata(fileName, content);
        if (!result.IsSuccess)
            return new TextExtractionResult(false, string.Empty, result.ErrorMessage);

        var metadata = ConvertMetadata(result.Metadata);
        return new TextExtractionResult(true, result.Text ?? string.Empty, null, metadata);
    }

    private static IReadOnlyDictionary<string, string>? ConvertMetadata(object? metadata)
    {
        if (metadata is null) return null;
        try
        {
            var json = JsonSerializer.Serialize(metadata);
            using var doc = JsonDocument.Parse(json);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.ToString();
            }
            return dict;
        }
        catch
        {
            return null;
        }
    }
}
