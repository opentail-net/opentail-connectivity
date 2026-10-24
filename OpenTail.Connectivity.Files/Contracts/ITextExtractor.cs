namespace OpenTail.Connectivity.Files.Contracts;

public sealed record TextExtractionResult(
    bool Success,
    string Text,
    string? ErrorMessage = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool NotText = false)
{
    // NotText: the failure means the file has no text to read (a binary file, or an image with no OCR), not that reading went
    // wrong. The pipeline counts it as skipped rather than failed.
}

public interface ITextExtractor
{
    TextExtractionResult Extract(string fileName, byte[] content);
}
