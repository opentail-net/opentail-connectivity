using System.Formats.Tar;
using System.IO.Compression;
using OpenTail.Connectivity.Files;
using OpenTail.Connectivity.Files.Archives;

namespace OpenTail.Connectivity.Tests.Archives;

public sealed class ArchiveExpanderTests
{
    private static readonly DateTimeOffset Modified = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    internal static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                stream.Write(content);
            }
        }
        return ms.ToArray();
    }

    internal static byte[] Zip(params (string Name, string Text)[] entries) =>
        Zip(entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Text))).ToArray());

    private static byte[] Tar(Action<TarWriter> write)
    {
        using var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true)) write(writer);
        return ms.ToArray();
    }

    private static void TarFile(TarWriter writer, string name, string text) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });

    private static byte[] Gzip(byte[] content)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(content);
        return ms.ToArray();
    }

    internal static SourceItem Item(string name, IReadOnlyDictionary<string, string>? metadata = null) =>
        new("s3://bucket/" + name, name, Path.GetExtension(name).TrimStart('.'), 1, Modified, "acme", metadata);

    private static List<string> Paths(ItemExpansion expansion) => expansion.Items.Select(i => i.Item.Metadata![ArchiveExpander.PathKey]).ToList();

    [Fact]
    public void Zip_entries_become_children_with_stable_ids_and_the_archives_permissions()
    {
        var meta = new Dictionary<string, string> { [SourceKeys.Version] = "etag-1", [SourceKeys.AclCaptured] = "true", [SourceKeys.AclVisibility] = "private" };
        var zip = Zip(("q3/sales.txt", "sales"), ("q3/", ""), ("readme.md", "hello"));

        var expansion = new ArchiveExpander().Expand(Item("report.zip", meta), zip);

        Assert.Equal(["s3://bucket/report.zip!/q3/sales.txt", "s3://bucket/report.zip!/readme.md"], expansion.Items.Select(i => i.Item.Id));
        Assert.Empty(expansion.Refused); // the folder entry is skipped quietly
        var child = expansion.Items[0].Item;
        Assert.Equal("sales.txt", child.DisplayName);
        Assert.Equal("txt", child.Extension);
        Assert.Equal(Modified, child.Modified);
        Assert.Equal("acme", child.TenantContext);
        Assert.Equal("private", child.Metadata![SourceKeys.AclVisibility]);
        Assert.False(child.Metadata.ContainsKey(SourceKeys.Version)); // the archive's version is not the entry's
        Assert.Equal("s3://bucket/report.zip", child.Metadata[ArchiveExpander.ParentKey]);
        Assert.Equal("sales", Encoding.UTF8.GetString(expansion.Items[0].Content));

        // Same bytes, same ids.
        Assert.Equal(expansion.Items.Select(i => i.Item.Id), new ArchiveExpander().Expand(Item("report.zip", meta), zip).Items.Select(i => i.Item.Id));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x.txt")]
    [InlineData("C:/Windows/x.txt")]
    [InlineData("c:evil.txt")]
    [InlineData("bad\u0001name.txt")]
    [InlineData("inner.zip!/pose.txt")]
    public void Unsafe_entry_paths_are_refused_and_the_rest_go_through(string name)
    {
        var expansion = new ArchiveExpander().Expand(Item("a.zip"), Zip((name, "x"), ("ok.txt", "fine")));

        Assert.Equal(["ok.txt"], Paths(expansion));
        var refused = Assert.Single(expansion.Refused);
        Assert.DoesNotContain(refused.Path, c => char.IsControl(c));
    }

    [Fact]
    public void Dot_segments_and_backslashes_are_normalised()
    {
        var expansion = new ArchiveExpander().Expand(Item("a.zip"), Zip(("./docs//b\\c.txt", "x")));
        Assert.Equal(["docs/b/c.txt"], Paths(expansion));
    }

    [Fact]
    public void A_duplicate_path_is_refused_so_ids_stay_unique()
    {
        var expansion = new ArchiveExpander().Expand(Item("a.zip"), Zip(("a.txt", "one"), ("./a.txt", "two")));

        Assert.Equal("one", Encoding.UTF8.GetString(Assert.Single(expansion.Items).Content));
        Assert.Contains("same path", Assert.Single(expansion.Refused).Reason);
    }

    [Fact]
    public void A_zip_bomb_is_refused_by_its_compression_ratio()
    {
        var zip = Zip(("zeros.txt", new byte[20 * 1024 * 1024]));
        Assert.True(zip.Length < 100_000); // the point of a bomb

        var ex = Assert.Throws<InvalidDataException>(() => new ArchiveExpander().Expand(Item("bomb.zip"), zip));
        Assert.Contains("archive bomb", ex.Message);
    }

    [Fact]
    public void Total_decompressed_bytes_are_counted_as_read_not_as_declared()
    {
        var zip = Zip(("a.txt", new string('a', 600)), ("b.txt", new string('b', 600)));

        var ex = Assert.Throws<InvalidDataException>(() => new ArchiveExpander(new ArchiveOptions(MaxTotalBytes: 1000)).Expand(Item("a.zip"), zip));
        Assert.Contains("1000 bytes", ex.Message);
    }

    [Fact]
    public void Too_many_entries_refuses_the_archive_before_reading_any()
    {
        var zip = Zip(Enumerable.Range(0, 11).Select(i => ($"f{i}.txt", "x")).ToArray());

        var ex = Assert.Throws<InvalidDataException>(() => new ArchiveExpander(new ArchiveOptions(MaxEntries: 10)).Expand(Item("a.zip"), zip));
        Assert.Contains("11 entries", ex.Message);
    }

    [Fact]
    public void An_entry_over_the_per_entry_cap_is_refused_alone()
    {
        var zip = Zip(("big.txt", new string('x', 500)), ("small.txt", "ok"));

        var expansion = new ArchiveExpander(new ArchiveOptions(MaxEntryBytes: 100)).Expand(Item("a.zip"), zip);

        Assert.Equal(["small.txt"], Paths(expansion));
        Assert.Contains("larger than 100 bytes", Assert.Single(expansion.Refused).Reason);
    }

    [Fact]
    public void A_corrupt_archive_is_refused_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidDataException>(() => new ArchiveExpander().Expand(Item("broken.zip"), Encoding.UTF8.GetBytes("not a zip at all")));
        Assert.Contains("'broken.zip' is not a readable archive", ex.Message);
    }

    [Fact]
    public void A_corrupt_entry_is_refused_and_the_others_go_through()
    {
        var zip = Zip(("bad.txt", new string('q', 4000)), ("good.txt", "fine"));
        // The first entry's data starts after its 30-byte local header and name; overwrite the middle of it.
        var corrupt = (byte[])zip.Clone();
        for (var i = 30 + "bad.txt".Length + 2; i < 30 + "bad.txt".Length + 12; i++) corrupt[i] ^= 0xFF;

        var expansion = new ArchiveExpander().Expand(Item("a.zip"), corrupt);

        Assert.Equal(["good.txt"], Paths(expansion));
        Assert.StartsWith("unreadable", Assert.Single(expansion.Refused).Reason);
    }

    [Fact]
    public void Nested_archives_are_refused_at_the_default_depth_and_expanded_when_allowed()
    {
        var inner = Zip(("deep.txt", "deep"));
        var outer = Zip(("inner.zip", inner), ("top.txt", Encoding.UTF8.GetBytes("top")));

        var flat = new ArchiveExpander().Expand(Item("outer.zip"), outer);
        Assert.Equal(["top.txt"], Paths(flat));
        Assert.Contains("archive inside an archive", Assert.Single(flat.Refused).Reason);

        var nested = new ArchiveExpander(new ArchiveOptions(MaxDepth: 2)).Expand(Item("outer.zip"), outer);
        Assert.Equal(["s3://bucket/outer.zip!/inner.zip!/deep.txt", "s3://bucket/outer.zip!/top.txt"], nested.Items.Select(i => i.Item.Id));
        Assert.Equal("inner.zip!/deep.txt", nested.Items[0].Item.Metadata![ArchiveExpander.PathKey]);
        Assert.Equal("s3://bucket/outer.zip", nested.Items[0].Item.Metadata![ArchiveExpander.ParentKey]);
    }

    [Fact]
    public void A_corrupt_nested_archive_is_one_refused_entry()
    {
        var outer = Zip(("inner.zip", Encoding.UTF8.GetBytes("garbage")), ("top.txt", Encoding.UTF8.GetBytes("top")));

        var expansion = new ArchiveExpander(new ArchiveOptions(MaxDepth: 2)).Expand(Item("outer.zip"), outer);

        Assert.Equal(["top.txt"], Paths(expansion));
        Assert.Equal("inner.zip", Assert.Single(expansion.Refused).Path);
    }

    [Fact]
    public void A_bomb_inside_a_nested_archive_refuses_the_whole_item()
    {
        var outer = Zip(("inner.zip", Zip(("zeros.txt", new byte[20 * 1024 * 1024]))));

        Assert.Throws<InvalidDataException>(() => new ArchiveExpander(new ArchiveOptions(MaxDepth: 2)).Expand(Item("outer.zip"), outer));
    }

    [Fact]
    public void Tar_regular_files_expand_and_links_and_traversal_are_refused()
    {
        var tar = Tar(w =>
        {
            TarFile(w, "docs/a.txt", "alpha");
            w.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "docs/sub/"));
            w.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "passwd") { LinkName = "/etc/passwd" });
            w.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "hard") { LinkName = "docs/a.txt" });
            TarFile(w, "../escape.txt", "nope");
        });

        var expansion = new ArchiveExpander().Expand(Item("bundle.tar"), tar);

        Assert.Equal(["docs/a.txt"], Paths(expansion));
        Assert.Equal(3, expansion.Refused.Count);
        Assert.Contains(expansion.Refused, r => r.Path == "passwd" && r.Reason.Contains("links are not followed"));
    }

    [Fact]
    public void Tar_gz_is_read_compressed_or_already_decompressed()
    {
        var tar = Tar(w => TarFile(w, "a.txt", "alpha"));

        Assert.Equal(["a.txt"], Paths(new ArchiveExpander().Expand(Item("x.tar.gz"), Gzip(tar))));
        Assert.Equal(["a.txt"], Paths(new ArchiveExpander().Expand(Item("x.tgz"), Gzip(tar))));
        // Object storage decompresses .gz on fetch, so the same name can arrive as a plain tar.
        Assert.Equal(["a.txt"], Paths(new ArchiveExpander().Expand(Item("x.tar.gz"), tar)));
    }

    [Fact]
    public void A_gzip_bomb_is_refused()
    {
        var tar = Tar(w => w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "zeros") { DataStream = new MemoryStream(new byte[20 * 1024 * 1024]) }));

        var ex = Assert.Throws<InvalidDataException>(() => new ArchiveExpander().Expand(Item("x.tar.gz"), Gzip(tar)));
        Assert.Contains("archive bomb", ex.Message);
    }

    [Fact]
    public void Too_many_tar_entries_refuses_the_archive()
    {
        var tar = Tar(w => { for (var i = 0; i < 11; i++) TarFile(w, $"f{i}.txt", "x"); });

        Assert.Throws<InvalidDataException>(() => new ArchiveExpander(new ArchiveOptions(MaxEntries: 10)).Expand(Item("x.tar"), tar));
    }

    [Fact]
    public void Entries_the_filter_does_not_support_are_refused()
    {
        var options = new ArchiveOptions(EntryFilter: new FileFilter());
        var expansion = new ArchiveExpander(options).Expand(Item("a.zip"), Zip(("tool.exe", "MZ"), ("notes.txt", "ok")));

        Assert.Equal(["notes.txt"], Paths(expansion));
        Assert.Equal("tool.exe", Assert.Single(expansion.Refused).Path);
    }

    [Fact]
    public void Expansion_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new ArchiveExpander().Expand(Item("a.zip"), Zip(("a.txt", "x")), cts.Token));
    }

    [Theory]
    [InlineData("report.zip", true)]
    [InlineData("REPORT.ZIP", true)]
    [InlineData("x.tar", true)]
    [InlineData("x.tar.gz", true)]
    [InlineData("x.tgz", true)]
    [InlineData("notes.txt.gz", false)]
    [InlineData("notes.txt", false)]
    public void CanExpand_goes_by_name(string name, bool expected) => Assert.Equal(expected, new ArchiveExpander().CanExpand(Item(name)));
}
