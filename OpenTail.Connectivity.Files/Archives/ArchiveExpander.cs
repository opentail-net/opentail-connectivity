using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;

namespace OpenTail.Connectivity.Files.Archives;

/// <param name="MaxEntries">Entries in one archive, counted before any is read (zip) or as they come (tar). More and the item is refused.</param>
/// <param name="MaxEntryBytes">A larger entry is refused. At most this much (plus one read) is decompressed for it.</param>
/// <param name="MaxTotalBytes">Bytes decompressed for one fetched item, nested archives included. More and the item is refused.</param>
/// <param name="MaxCompressionRatio">
/// An entry, or a whole <c>.tar.gz</c>, expanding beyond this many times its compressed size is taken for an archive bomb and the
/// item is refused. Checked once the output passes 1 MB, so small, very compressible files go through.
/// </param>
/// <param name="MaxDepth">1 expands the item's own entries and refuses archives inside it; 2 also expands those, and so on.</param>
/// <param name="EntryFilter">When set, entries whose extension it does not support are refused instead of extracted.</param>
public sealed record ArchiveOptions(
    int MaxEntries = 1_000,
    long MaxEntryBytes = 50L * 1024 * 1024,
    long MaxTotalBytes = 200L * 1024 * 1024,
    int MaxCompressionRatio = 100,
    int MaxDepth = 1,
    FileFilter? EntryFilter = null);

/// <summary>
/// Expands <c>.zip</c>, <c>.tar</c>, <c>.tar.gz</c> and <c>.tgz</c> items into one child per file entry, with the id
/// <c>&lt;item id&gt;!/&lt;entry path&gt;</c>. Archive content is untrusted: sizes in headers are never believed (bytes are counted as
/// they are decompressed), paths that are absolute, climb with <c>..</c> or contain control characters are refused, links are
/// not followed, and nothing is written to disk.
/// </summary>
/// <remarks>
/// Children carry the item's metadata (its permissions above all: an entry is as restricted as its archive) without
/// <see cref="SourceKeys.Version"/>, plus <see cref="ParentKey"/> and <see cref="PathKey"/>. Their modified time is the item's,
/// because zip times have no time zone. Default filters leave archives out; add <c>.zip</c> and the others to a connector's
/// <c>ExtraExtensions</c> to offer them.
/// </remarks>
public sealed class ArchiveExpander(ArchiveOptions? options = null) : IItemExpander
{
    /// <summary>The id of the fetched item the entry came from.</summary>
    public const string ParentKey = "source.archive.parent";

    /// <summary>The entry's path inside that item, nested levels joined with <c>!/</c>.</summary>
    public const string PathKey = "source.archive.path";

    private const long RatioFloorBytes = 1024 * 1024;
    private const int MaxPathLength = 1024;

    private readonly ArchiveOptions _options = options ?? new ArchiveOptions();

    private enum Kind { None, Zip, Tar, TarGz }

    public bool CanExpand(SourceItem item) => KindOf(item.DisplayName) != Kind.None || KindOf("x." + item.Extension) != Kind.None;

    public ItemExpansion Expand(SourceItem item, byte[] content, CancellationToken cancellationToken = default)
    {
        var kind = KindOf(item.DisplayName);
        if (kind == Kind.None) kind = KindOf("x." + item.Extension);
        if (kind == Kind.None) throw new InvalidDataException($"'{item.DisplayName}' is not an archive this expander reads.");

        var run = new Expansion(item, cancellationToken);
        try
        {
            ExpandInto(run, item.Id, item.DisplayName, content, kind, depth: 1);
        }
        catch (LimitException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        return new ItemExpansion(run.Items, run.Refused);
    }

    private sealed class Expansion(SourceItem root, CancellationToken ct)
    {
        public SourceItem Root { get; } = root;
        public CancellationToken Ct { get; } = ct;
        public List<ExpandedItem> Items { get; } = [];
        public List<RefusedEntry> Refused { get; } = [];
        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
        public long TotalBytes { get; set; }
    }

    /// <summary>A limit that refuses the whole item, nested or not: never caught below the top.</summary>
    private sealed class LimitException(string message) : Exception(message);

    private sealed class EntryTooLargeException : Exception;

    private void ExpandInto(Expansion run, string containerId, string name, byte[] content, Kind kind, int depth)
    {
        try
        {
            switch (kind)
            {
                case Kind.Zip:
                    ExpandZip(run, containerId, content, depth);
                    break;
                case Kind.Tar:
                    ExpandTar(run, containerId, new MemoryStream(content, writable: false), depth);
                    break;
                case Kind.TarGz:
                    // A connector may have decompressed it already (object storage does for .gz).
                    var tar = content is [0x1F, 0x8B, ..] ? Gunzip(name, content) : content;
                    ExpandTar(run, containerId, new MemoryStream(tar, writable: false), depth);
                    break;
            }
        }
        catch (Exception ex) when (ex is not LimitException and not OperationCanceledException)
        {
            throw new InvalidDataException($"'{Printable(name)}' is not a readable archive: {ex.Message}", ex);
        }
    }

    private void ExpandZip(Expansion run, string containerId, byte[] content, int depth)
    {
        using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
        if (zip.Entries.Count > _options.MaxEntries)
            throw new LimitException($"The archive has {zip.Entries.Count} entries; the limit is {_options.MaxEntries}.");

        foreach (var entry in zip.Entries)
        {
            run.Ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // a folder
            if (entry.IsEncrypted)
            {
                Refuse(run, containerId, entry.FullName, "encrypted entries are not read");
                continue;
            }
            // The central directory's compressed size is the archive's claim too, but it cannot exceed the bytes we hold.
            var compressed = Math.Min(entry.CompressedLength, content.Length);
            AddEntry(run, containerId, entry.FullName, depth, entry.Open, disposeStream: true, compressed);
        }
    }

    private void ExpandTar(Expansion run, string containerId, Stream stream, int depth)
    {
        using var reader = new TarReader(stream, leaveOpen: false);
        var count = 0;
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            run.Ct.ThrowIfCancellationRequested();
            if (++count > _options.MaxEntries)
                throw new LimitException($"The archive has more than {_options.MaxEntries} entries, the limit.");

            switch (entry.EntryType)
            {
                case TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes:
                    continue;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    // The reader owns the data stream, so it is not disposed here.
                    AddEntry(run, containerId, entry.Name, depth, () => entry.DataStream ?? Stream.Null, disposeStream: false, compressed: null);
                    break;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    Refuse(run, containerId, entry.Name, "a link; links are not followed");
                    break;
                default:
                    Refuse(run, containerId, entry.Name, $"not a regular file ({entry.EntryType})");
                    break;
            }
        }
    }

    private void AddEntry(Expansion run, string containerId, string rawName, int depth, Func<Stream> open, bool disposeStream, long? compressed)
    {
        if (!TryEntryPath(rawName, out var path, out var reason))
        {
            Refuse(run, containerId, rawName, reason);
            return;
        }

        var id = containerId + "!/" + path;
        if (!run.Ids.Add(id))
        {
            Refuse(run, containerId, path, "another entry has the same path");
            return;
        }

        var name = path[(path.LastIndexOf('/') + 1)..];
        var nested = KindOf(name);
        if (nested != Kind.None && depth >= Math.Max(1, _options.MaxDepth))
        {
            Refuse(run, containerId, path, $"an archive inside an archive; only {Math.Max(1, _options.MaxDepth)} level(s) are expanded");
            return;
        }
        if (nested == Kind.None && _options.EntryFilter is { } filter && !filter.HasSupportedExtension(name))
        {
            Refuse(run, containerId, path, "the extension is not supported");
            return;
        }

        byte[] bytes;
        try
        {
            var stream = open();
            try
            {
                bytes = ReadCapped(run, stream, path, compressed);
            }
            finally
            {
                if (disposeStream) stream.Dispose();
            }
        }
        catch (EntryTooLargeException)
        {
            Refuse(run, containerId, path, $"larger than {_options.MaxEntryBytes.ToString(CultureInfo.InvariantCulture)} bytes");
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            Refuse(run, containerId, path, "unreadable: " + ex.Message); // a corrupt entry, or an unsupported compression method
            return;
        }

        if (nested != Kind.None)
        {
            // A corrupt inner archive is one bad entry; take back whatever it yielded before it failed.
            var (items, refused) = (run.Items.Count, run.Refused.Count);
            try
            {
                ExpandInto(run, id, name, bytes, nested, depth + 1);
            }
            catch (InvalidDataException ex)
            {
                foreach (var added in run.Items.Skip(items)) run.Ids.Remove(added.Item.Id);
                run.Items.RemoveRange(items, run.Items.Count - items);
                run.Refused.RemoveRange(refused, run.Refused.Count - refused);
                Refuse(run, containerId, path, ex.Message);
            }
            return;
        }

        run.Items.Add(new ExpandedItem(Child(run.Root, id, name, bytes.Length), bytes));
    }

    private byte[] ReadCapped(Expansion run, Stream stream, string path, long? compressed)
    {
        var buffer = new byte[81920];
        using var output = new MemoryStream();
        int n;
        while ((n = stream.Read(buffer)) > 0)
        {
            run.Ct.ThrowIfCancellationRequested();
            run.TotalBytes += n;
            if (run.TotalBytes > _options.MaxTotalBytes)
                throw new LimitException($"More than {_options.MaxTotalBytes.ToString(CultureInfo.InvariantCulture)} bytes would be decompressed; the archive is refused.");
            if (output.Length + n > _options.MaxEntryBytes)
                throw new EntryTooLargeException();
            output.Write(buffer, 0, n);
            if (compressed is { } c && output.Length > RatioFloorBytes && output.Length > (double)_options.MaxCompressionRatio * Math.Max(1, c))
                throw new LimitException($"'{Printable(path)}' expands more than {_options.MaxCompressionRatio}:1, like an archive bomb; the archive is refused.");
        }
        return output.ToArray();
    }

    private byte[] Gunzip(string name, byte[] content)
    {
        using var gzip = new GZipStream(new MemoryStream(content, writable: false), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = gzip.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, n);
            if (output.Length > _options.MaxTotalBytes)
                throw new LimitException($"'{Printable(name)}' decompresses to more than {_options.MaxTotalBytes.ToString(CultureInfo.InvariantCulture)} bytes; the archive is refused.");
            if (output.Length > RatioFloorBytes && output.Length > (double)_options.MaxCompressionRatio * content.Length)
                throw new LimitException($"'{Printable(name)}' expands more than {_options.MaxCompressionRatio}:1, like an archive bomb; the archive is refused.");
        }
        return output.ToArray();
    }

    private static SourceItem Child(SourceItem root, string id, string name, long size)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.Metadata is not null)
            foreach (var kv in root.Metadata)
                if (!string.Equals(kv.Key, SourceKeys.Version, StringComparison.OrdinalIgnoreCase)) meta[kv.Key] = kv.Value;
        meta[ParentKey] = root.Id;
        meta[PathKey] = id[(root.Id.Length + 2)..];
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return new SourceItem(id, name, extension, size, root.Modified, root.TenantContext, meta);
    }

    private static void Refuse(Expansion run, string containerId, string rawName, string reason)
    {
        var inside = containerId.Length > run.Root.Id.Length ? containerId[(run.Root.Id.Length + 2)..] + "!/" : "";
        run.Refused.Add(new RefusedEntry(inside + Printable(rawName), reason));
    }

    /// <summary>
    /// The entry's path with <c>/</c> separators and no empty or <c>.</c> segments, or false with the reason. Refuses absolute
    /// paths, <c>..</c>, control characters and <c>!/</c> (it separates levels in ids, so it would let one entry pose as another).
    /// </summary>
    internal static bool TryEntryPath(string raw, out string path, out string reason)
    {
        path = "";
        reason = "";
        if (raw.Length > MaxPathLength)
        {
            reason = $"a name longer than {MaxPathLength} characters";
            return false;
        }
        if (raw.Any(char.IsControl))
        {
            reason = "a name with control characters";
            return false;
        }

        var p = raw.Replace('\\', '/');
        if (p.StartsWith('/') || (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0])))
        {
            reason = "an absolute path";
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in p.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                reason = "a path that climbs out of the archive ('..')";
                return false;
            }
            segments.Add(segment);
        }

        var joined = string.Join('/', segments);
        if (joined.Length == 0)
        {
            reason = "an empty name";
            return false;
        }
        if (joined.Contains("!/", StringComparison.Ordinal))
        {
            reason = "a name containing '!/', which separates archive levels in ids";
            return false;
        }

        path = joined;
        return true;
    }

    private static Kind KindOf(string name)
    {
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return Kind.Zip;
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return Kind.Tar;
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return Kind.TarGz;
        return Kind.None;
    }

    private static string Printable(string s)
    {
        var chars = s.Take(200).Select(c => char.IsControl(c) ? '?' : c).ToArray();
        return new string(chars) + (s.Length > 200 ? "…" : "");
    }
}
