using System.IO.Compression;
using System.Text;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Extraction;

namespace OpenTail.Connectivity.Storage;

public sealed class ObjectStoreReaderOptions
{
    /// <summary>
    /// Key prefixes that may be listed and read. Required. Compared exactly, as given: object keys are not paths, so nothing is
    /// normalised (<c>..</c> and <c>//</c> are ordinary characters). An empty string allows the whole container.
    /// </summary>
    public IReadOnlyList<string> Prefixes { get; init; } = [];

    /// <summary>Bytes read from the store per object. Default 256 KB.</summary>
    public long MaxReadBytes { get; init; } = 256 * 1024;

    /// <summary>Bytes a gzipped object may expand to; decompression stops there. Default 4 × <see cref="MaxReadBytes"/>.</summary>
    public long? MaxDecompressedBytes { get; init; }

    /// <summary>Objects returned per list call. Default 200.</summary>
    public int MaxListItems { get; init; } = 200;

    /// <summary>Read objects whose storage class charges extra per read. Off by default. Objects that need a restore are never read.</summary>
    public bool ReadColdStorage { get; init; }

    /// <summary>Turns PDF, Office and other files into text. Default <see cref="TransformationsTextExtractor"/>.</summary>
    public ITextExtractor? Extractor { get; init; }
}

public sealed record ObjectListResult(IReadOnlyList<ObjectInfo> Objects, string? NextResumeToken);

/// <param name="Truncated">The text stops early: the object (or its decompressed form) was longer than the limits.</param>
/// <param name="Note">Why the text is partial or empty, for the reader.</param>
public sealed record ObjectText(
    string Key,
    string Text,
    bool Truncated,
    long Size,
    DateTimeOffset LastModified,
    string? ContentType,
    string? Note);

/// <summary>The request is outside what the reader allows (prefix, storage class, format). Nothing was read.</summary>
public sealed class ObjectStoreRefusedException(string message) : ArgumentException(message);

/// <summary>
/// Lists and reads objects for live tools, inside a prefix allow-list, with capped reads and gzip that cannot expand without
/// bound. The container is fixed by the client; callers never choose it.
/// </summary>
public sealed class ObjectStoreReader
{
    private static readonly HashSet<string> Unsupported = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "tar", "tgz", "7z", "rar", "bz2", "xz", "zst", "parquet", "avro", "orc",
    };

    private static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "txt", "text", "log", "csv", "tsv", "json", "jsonl", "ndjson", "md", "markdown", "xml", "html", "htm", "yaml", "yml",
        "ini", "cfg", "conf", "sql", "eml",
    };

    private readonly IObjectStoreClient client;
    private readonly ObjectStoreReaderOptions options;
    private readonly ITextExtractor extractor;

    public ObjectStoreReader(IObjectStoreClient client, ObjectStoreReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Prefixes.Count == 0) throw new ArgumentException("ObjectStoreReaderOptions.Prefixes needs at least one prefix (\"\" allows the whole container).");
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxReadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxListItems, 1);
        this.client = client;
        this.options = options;
        extractor = options.Extractor ?? new TransformationsTextExtractor();
    }

    public IReadOnlyList<string> Prefixes => options.Prefixes;

    /// <summary>Whether <paramref name="key"/> starts with an allowed prefix.</summary>
    public bool IsAllowed(string key) => options.Prefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// One page of objects under <paramref name="prefix"/> (default: the only allowed prefix). Folder placeholders are left out.
    /// </summary>
    public async Task<ObjectListResult> ListAsync(string? prefix = null, string? resumeToken = null, CancellationToken cancellationToken = default)
    {
        prefix ??= options.Prefixes.Count == 1
            ? options.Prefixes[0]
            : throw new ObjectStoreRefusedException($"Name a prefix: one of {string.Join(", ", options.Prefixes.Select(p => $"'{p}'"))}.");
        if (!IsAllowed(prefix)) throw Outside(prefix);

        // A page can hold only folder placeholders; read on a few pages rather than hand back an empty page with a token.
        for (var pages = 0; ; pages++)
        {
            var page = await client.ListAsync(prefix, resumeToken, options.MaxListItems, cancellationToken).ConfigureAwait(false);
            // A store that answers with keys outside the prefix (a bug, or a compatible store that ignores it) must not leak them.
            var objects = page.Objects.Where(o => !o.IsFolderPlaceholder && o.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (objects.Count > 0 || page.NextResumeToken is null || pages == 4) return new ObjectListResult(objects, page.NextResumeToken);
            resumeToken = page.NextResumeToken;
        }
    }

    /// <summary>Reads one object as text. Throws <see cref="ObjectStoreRefusedException"/> before any read when it is not allowed.</summary>
    /// <remarks>
    /// Text formats (including HTML, XML and JSON) come back as their source, decoded but not rendered: HTML keeps its
    /// tags, scripts and styles. That keeps capped partial reads useful. Binary formats (PDF, Office ...) go through the
    /// configured extractor and must fit the read limit whole.
    /// </remarks>
    public async Task<ObjectText> ReadTextAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!IsAllowed(key)) throw Outside(key);

        var (name, gzipped) = key.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? (key[..^3], true) : (key, false);
        var extension = Extension(name);
        if (Unsupported.Contains(extension))
            throw new ObjectStoreRefusedException($"'.{extension}' objects are not read: archives and columnar data are not converted to text.");

        var info = await client.HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw new ObjectNotFoundException(key);
        if (info.IsFolderPlaceholder) throw new ObjectStoreRefusedException($"'{key}' is a folder placeholder; list it instead.");
        if (info.NeedsRestore)
            throw new ObjectStoreRefusedException($"'{key}' is in storage class {info.StorageClass} and needs a restore first; restores are never started from here.");
        if (info.CostsExtraToRead && !options.ReadColdStorage)
            throw new ObjectStoreRefusedException($"'{key}' is in storage class {info.StorageClass}, which charges per read; the operator has not allowed cold reads.");

        var content = await client.ReadAsync(key, options.MaxReadBytes, cancellationToken).ConfigureAwait(false);
        var bytes = content.Bytes;
        var truncated = content.Truncated;
        string? note = truncated ? $"Only the first {options.MaxReadBytes} bytes of {info.Size} were read." : null;

        gzipped |= string.Equals(content.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase);
        if (gzipped)
        {
            var limit = options.MaxDecompressedBytes ?? options.MaxReadBytes * 4;
            (bytes, var cut) = Gunzip(bytes, limit, inputTruncated: truncated);
            if (cut) note = $"Decompressed text was cut at {limit} bytes.";
            truncated |= cut;
        }

        if (PlainText.Contains(extension))
            return Result(Decode(bytes, truncated), note);

        if (truncated)
            return Result("", $"'.{extension}' files can only be read whole, and this one is larger than the read limit ({options.MaxReadBytes} bytes).");

        var extracted = extractor.Extract(Path.GetFileName(name), bytes);
        return extracted.Success
            ? Result(extracted.Text, note)
            : Result("", "Text could not be extracted: " + extracted.ErrorMessage);

        ObjectText Result(string text, string? why) => new(key, text, truncated, info.Size, info.LastModified, content.ContentType, why);
    }

    private ObjectStoreRefusedException Outside(string key) =>
        new($"'{key}' is outside the allowed prefixes ({string.Join(", ", options.Prefixes.Select(p => $"'{p}'"))}).");

    private static string Extension(string key)
    {
        var slash = key.LastIndexOf('/');
        var dot = key.LastIndexOf('.');
        return dot > slash && dot < key.Length - 1 ? key[(dot + 1)..] : "";
    }

    /// <summary>Decompresses up to <paramref name="limit"/> bytes. A cut-off input stops cleanly at what could be decoded.</summary>
    internal static (byte[] Bytes, bool Cut) Gunzip(byte[] input, long limit, bool inputTruncated)
    {
        using var output = new MemoryStream();
        using var gzip = new GZipStream(new MemoryStream(input), CompressionMode.Decompress);
        var buffer = new byte[81920];
        var cut = false;
        try
        {
            int n;
            while ((n = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                var room = limit - output.Length;
                if (n >= room)
                {
                    output.Write(buffer, 0, (int)room);
                    // Exactly at the limit is a cut only if more data follows.
                    cut = n > room || gzip.Read(buffer, 0, 1) > 0;
                    break;
                }
                output.Write(buffer, 0, n);
            }
        }
        catch (Exception ex) when (inputTruncated && ex is InvalidDataException or EndOfStreamException)
        {
            // The compressed bytes stop early (the read was capped); keep what was decoded. The caller already marks it truncated.
        }
        catch (InvalidDataException ex)
        {
            throw new ObjectStoreRefusedException("The object is not valid gzip: " + ex.Message);
        }
        return (output.ToArray(), cut);
    }

    private static string Decode(byte[] bytes, bool truncated)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        // A cut can split a multi-byte character; drop the replacement character it leaves at the end.
        return truncated ? text.TrimEnd('�') : text;
    }
}
