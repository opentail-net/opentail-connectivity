using OpenTail.Connectivity.Files.Connectors;

namespace OpenTail.Connectivity.Files;

/// <summary>Which files a file-like connector (folder, drive, bucket) offers.</summary>
/// <param name="IncludePatterns">When set, only paths matching one of these. <c>*</c> stays inside a path segment, <c>**</c> crosses them; case-insensitive; <c>\</c> is <c>/</c>.</param>
/// <param name="ExcludePatterns">Paths matching any of these are left out, before includes are looked at.</param>
/// <param name="SupportedExtensions">Replaces <see cref="FileFilter.DefaultExtensions"/>. With or without the dot.</param>
/// <param name="ExtraExtensions">Added to the supported extensions.</param>
/// <param name="MaxFileSizeBytes">Larger files are left out.</param>
public sealed record FileFilterOptions(
    IReadOnlyList<string>? IncludePatterns = null,
    IReadOnlyList<string>? ExcludePatterns = null,
    IReadOnlyList<string>? SupportedExtensions = null,
    IReadOnlyList<string>? ExtraExtensions = null,
    long? MaxFileSizeBytes = null);

/// <summary>
/// The folder connector's file rules, shared so every file-like connector applies them the same way. Paths are relative to
/// whatever root the connector has (a folder, a drive, a bucket prefix).
/// </summary>
public sealed class FileFilter
{
    /// <summary>The extensions offered when <see cref="FileFilterOptions.SupportedExtensions"/> is not set.</summary>
    public static readonly IReadOnlyList<string> DefaultExtensions =
    [
        // Documents
        ".txt", ".md", ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".csv",
        ".html", ".htm", ".json", ".xml", ".rtf", ".odt", ".pptx", ".ppt",
        // Code files (extracted as plain text)
        ".cs", ".py", ".ts", ".js", ".jsx", ".tsx", ".go", ".rs",
        ".rb", ".java", ".cpp", ".c", ".h",
        // Config / data files
        ".yaml", ".yml", ".toml", ".ipynb",
    ];

    private readonly HashSet<string> extensions;
    private readonly IReadOnlyList<string> include;
    private readonly IReadOnlyList<string> exclude;

    public FileFilter(FileFilterOptions? options = null)
    {
        Options = options ?? new FileFilterOptions();
        extensions = new HashSet<string>(Normalise(Options.SupportedExtensions) ?? DefaultExtensions, StringComparer.OrdinalIgnoreCase);
        foreach (var e in Normalise(Options.ExtraExtensions) ?? []) extensions.Add(e);
        include = Options.IncludePatterns ?? [];
        exclude = Options.ExcludePatterns ?? [];
    }

    public FileFilterOptions Options { get; }

    /// <summary>
    /// A short stable hash of the rules that decide which files are offered. Incremental connectors keep it with their cursor
    /// and list everything again when it changes, so a narrowed or widened filter takes effect on unchanged files.
    /// </summary>
    public string Fingerprint => Hash(
        "ext=" + string.Join(',', extensions.Select(e => e.ToLowerInvariant()).Order(StringComparer.Ordinal)),
        "inc=" + string.Join('\n', include.Order(StringComparer.Ordinal)),
        "exc=" + string.Join('\n', exclude.Order(StringComparer.Ordinal)),
        "max=" + Options.MaxFileSizeBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>SHA-256 of the parts (newline-joined), first 16 hex characters.</summary>
    public static string Hash(params string[] parts) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', parts))))[..16];

    /// <summary>Extension, size and patterns together.</summary>
    public bool Includes(string relativePath, long sizeBytes) =>
        HasSupportedExtension(relativePath) && WithinSize(sizeBytes) && MatchesPatterns(relativePath);

    /// <summary>The file's extension (from the last segment) is supported.</summary>
    public bool HasSupportedExtension(string path)
    {
        var name = path.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot >= 0 && extensions.Contains(name[dot..]);
    }

    public bool WithinSize(long sizeBytes) => Options.MaxFileSizeBytes is not { } max || sizeBytes <= max;

    /// <summary>Not excluded, and included when include patterns are set.</summary>
    public bool MatchesPatterns(string relativePath)
    {
        if (exclude.Any(p => WildcardMatcher.Matches(relativePath, p))) return false;
        return include.Count == 0 || include.Any(p => WildcardMatcher.Matches(relativePath, p));
    }

    private static IReadOnlyList<string>? Normalise(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0) return null;
        return raw
            .Where(ext => !string.IsNullOrWhiteSpace(ext))
            .Select(ext => ext.Trim())
            .Select(ext => ext.StartsWith('.') ? ext : "." + ext)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
