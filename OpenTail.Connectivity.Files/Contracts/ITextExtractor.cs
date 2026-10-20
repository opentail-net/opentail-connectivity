namespace OpenTail.Connectivity.Files.Contracts;

public sealed record TextExtractionResult(
    bool Success,
    string Text,
    string? ErrorMessage = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public interface ITextExtractor
{
    TextExtractionResult Extract(string fileName, byte[] content);
}
