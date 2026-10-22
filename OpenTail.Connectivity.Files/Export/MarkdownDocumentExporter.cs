namespace OpenTail.Connectivity.Files.Export;

public sealed class MarkdownDocumentExporter : IConnectivityExporter, IAsyncDisposable
{
    private static readonly char[] UnsafeChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private readonly string _outputDirectory;

    public MarkdownDocumentExporter(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        _outputDirectory = outputDirectory;
    }

    public async Task ExportAsync(NormalisedDocument doc, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var safeName = MakeSafeName(doc.Title);
        var shortId = doc.Id.Length > 8 ? doc.Id[..8] : doc.Id;
        var fileName = $"{safeName}-{shortId}.md";
        var filePath = Path.Combine(_outputDirectory, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {YamlScalar(doc.Id)}");
        sb.AppendLine($"source_ref: {YamlScalar(doc.SourceRef)}");
        sb.AppendLine($"source_type: {YamlScalar(doc.SourceType)}");
        sb.AppendLine($"tenant: {YamlScalar(doc.TenantContext)}");
        sb.AppendLine($"source_modified: {doc.SourceModified:O}");
        sb.AppendLine($"ingested_at: {doc.IngestedAt:O}");
        sb.AppendLine($"content_hash: {YamlScalar(doc.ContentHash)}");
        foreach (var (key, value) in doc.Metadata)
        {
            // A metadata key must not overwrite one of the fixed fields above.
            var safeKey = ReservedKeys.Contains(key) ? "meta_" + key : key;
            sb.AppendLine($"{YamlScalar(safeKey)}: {YamlScalar(value)}");
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {SingleLine(doc.Title)}");
        sb.AppendLine();
        sb.Append(doc.Body);

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, cancellationToken);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly HashSet<string> ReservedKeys = new(StringComparer.Ordinal)
    {
        "id", "source_ref", "source_type", "tenant", "source_modified", "ingested_at", "content_hash",
    };

    private static readonly HashSet<string> YamlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "y", "n", "null", "~",
    };

    // Writes a value as a plain YAML scalar only when it is unambiguously a string;
    // anything else becomes a double-quoted scalar with escapes, so newlines, colons,
    // quotes and '#' cannot add fields or change how a parser reads the value.
    internal static string YamlScalar(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (IsPlainSafe(value)) return value;

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c) || c is '\u2028' or '\u2029' or '\u0085' or '\uFEFF')
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static bool IsPlainSafe(string value)
    {
        if (YamlKeywords.Contains(value)) return false;
        var first = value[0];
        if (!(char.IsAsciiLetter(first) || first == '_' || first == '/' || first == '.')) return false;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or '@' or '+'))
                return false;
        }
        return true;
    }

    private static string SingleLine(string text) =>
        text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    private static string MakeSafeName(string title)
    {
        var safe = new string(title.Select(c => UnsafeChars.Contains(c) || char.IsControl(c) ? '-' : c).ToArray());
        safe = safe.Trim().Replace("  ", " ").Replace(' ', '-');
        if (safe.Length > 60) safe = safe[..60];
        return string.IsNullOrWhiteSpace(safe) ? "document" : safe;
    }
}
