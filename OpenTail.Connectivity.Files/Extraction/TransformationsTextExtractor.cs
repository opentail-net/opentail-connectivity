namespace OpenTail.Connectivity.Files.Extraction;

/// <summary>
/// The sync pipeline's extractor. The library's default safety gate applies (a document with High findings, such as Unicode tag
/// characters or hidden instructions, fails as quarantined; in ZIP and EML only that part is withheld). Hidden text is dropped, so
/// it is never indexed. Images and scanned pages are read when an OCR engine is given.
/// </summary>
public sealed class TransformationsTextExtractor : ITextExtractor
{
    private static readonly TT.TextExtractor Inner = new();
    private readonly TT.ExtractionOptions _options;

    /// <param name="ocr">The process's shared OCR engine (see <see cref="ConnectivityOcr"/>), or null for no OCR.</param>
    public TransformationsTextExtractor(TT.IOcrEngine? ocr = null)
    {
        _options = new TT.ExtractionOptions { HiddenText = TT.HiddenTextMode.Drop, OcrEngine = ocr };
    }

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        var result = Inner.GetTextWithMetadata(fileName, content, _options);
        if (!result.IsSuccess)
            return new TextExtractionResult(false, string.Empty, result.ErrorMessage,
                NotText: result.ErrorKind is TT.ExtractionErrorKind.Binary or TT.ExtractionErrorKind.NeedsOcr);

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
