using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using OpenTail.Connectivity.Storage;
using OpenTail.Connectivity.Storage.Sftp;
using Renci.SshNet.Common;

namespace OpenTail.Connectivity.Tests.Storage;

public sealed class SftpObjectStoreClientTests
{
    private static readonly string Fingerprint = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("test host key"))).TrimEnd('=');

    [Fact]
    public async Task Lists_in_key_order_and_skips_directories_symlinks_and_special_files()
    {
        var session = new FakeSession();
        session.AddDirectory("/exports", Entry("z.txt", regular: true), Entry("dir", directory: true),
            Entry("link", directory: true, symlink: true), Entry("socket", regular: false));
        session.AddDirectory("/exports/dir", Entry("a.txt", regular: true));
        var client = Client(session);

        var page = await client.ListAsync("", null, 10);

        Assert.Equal(new[] { "dir/a.txt", "z.txt" }, page.Objects.Select(o => o.Key));
        Assert.Equal($"9-{DateTime.UnixEpoch.AddDays(1).Ticks}", page.Objects[0].ETag);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), page.Objects[0].LastModified);
    }

    [Fact]
    public async Task Paging_resumes_after_last_key_and_prefix_filters()
    {
        var session = new FakeSession();
        session.AddDirectory("/exports", Entry("a.txt", regular: true), Entry("b.txt", regular: true), Entry("sub", directory: true));
        session.AddDirectory("/exports/sub", Entry("c.txt", regular: true));
        var client = Client(session);

        var first = await client.ListAsync("", null, 2);
        var second = await client.ListAsync("", first.NextResumeToken, 2);
        var filtered = await client.ListAsync("sub/", null, 10);

        Assert.Equal(new[] { "a.txt", "b.txt" }, first.Objects.Select(o => o.Key));
        Assert.Equal("b.txt", first.NextResumeToken);
        Assert.Equal(["sub/c.txt"], second.Objects.Select(o => o.Key));
        Assert.Equal(["sub/c.txt"], filtered.Objects.Select(o => o.Key));
    }

    [Fact]
    public async Task Connects_lazily_and_reuses_the_session()
    {
        var session = new FakeSession();
        session.AddDirectory("/exports", Entry("a.txt", regular: true));
        session.AddFile("/exports/a.txt", "text");
        var client = Client(session);

        Assert.Equal(0, session.ConnectCount);
        await client.HeadAsync("a.txt");
        await client.ListAsync("", null, 10);

        Assert.Equal(1, session.ConnectCount);
        client.Dispose();
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("folder/../secret.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("folder\\secret.txt")]
    public async Task Refuses_keys_and_prefixes_that_escape_the_root(string bad)
    {
        var client = Client(new FakeSession());

        await Assert.ThrowsAsync<ArgumentException>(() => client.HeadAsync(bad));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ReadAsync(bad, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAsync(bad, null, 10));
    }

    [Fact]
    public async Task Read_and_head_refuse_a_symlinked_parent_directory()
    {
        var session = new FakeSession();
        session.AddDirectory("/exports", Entry("linked-dir", directory: true, symlink: true));
        session.AddFile("/exports/linked-dir/secret.txt", "outside the root");
        var client = Client(session);

        await Assert.ThrowsAsync<SymbolicLinkRefusedException>(() => client.ReadAsync("linked-dir/secret.txt", 100));
        Assert.Null(await client.HeadAsync("linked-dir/secret.txt"));
        Assert.Empty(session.Opened);
    }

    [Fact]
    public async Task Read_refuses_a_symlinked_file()
    {
        var session = new FakeSession();
        session.AddFile("/exports/a.txt", "x");
        session.AddDirectory("/exports", Entry("a.txt", regular: true, symlink: true));
        var client = Client(session);

        await Assert.ThrowsAsync<SymbolicLinkRefusedException>(() => client.ReadAsync("a.txt", 100));
        Assert.Empty(session.Opened);
    }

    [Fact]
    public async Task Prefix_listing_starts_at_the_prefix_directory_and_prunes_other_branches()
    {
        var session = new FakeSession();
        session.AddDirectory("/exports", Entry("big", directory: true), Entry("sub", directory: true), Entry("subway", directory: true));
        session.AddDirectory("/exports/big", Entry("x.txt", regular: true));
        session.AddDirectory("/exports/sub", Entry("deep", directory: true), Entry("other", directory: true), Entry("c.txt", regular: true));
        session.AddDirectory("/exports/sub/deep", Entry("d.txt", regular: true));
        session.AddDirectory("/exports/sub/other", Entry("o.txt", regular: true));
        var client = Client(session);

        var page = await client.ListAsync("sub/deep", null, 10);
        Assert.Equal(["/exports/sub", "/exports/sub/deep"], session.Listed);
        session.Listed.Clear();
        var resumed = await client.ListAsync("sub/", "sub/deep/d.txt", 10);

        Assert.Equal(["sub/deep/d.txt"], page.Objects.Select(o => o.Key));
        Assert.Equal(["sub/other/o.txt"], resumed.Objects.Select(o => o.Key));
        Assert.Equal(["/exports/sub", "/exports/sub/deep", "/exports/sub/other"], session.Listed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Prefix_listing_of_a_missing_directory_is_empty()
    {
        var session = new FakeSession();
        session.AddDirectory("/exports", Entry("a.txt", regular: true));

        var page = await Client(session).ListAsync("nope/", null, 10);

        Assert.Empty(page.Objects);
    }

    [Fact]
    public async Task Head_returns_null_for_missing_file_and_read_throws_object_not_found()
    {
        var client = Client(new FakeSession());

        Assert.Null(await client.HeadAsync("missing.txt"));
        await Assert.ThrowsAsync<ObjectNotFoundException>(() => client.ReadAsync("missing.txt", 10));
    }

    [Fact]
    public async Task Read_is_capped_and_reports_a_weak_size_and_mtime_etag()
    {
        var session = new FakeSession();
        session.AddFile("/exports/a.txt", "hello world");
        var client = Client(session);

        var content = await client.ReadAsync("a.txt", 5);
        var info = await client.HeadAsync("a.txt");

        Assert.Equal("hello", Encoding.UTF8.GetString(content.Bytes));
        Assert.True(content.Truncated);
        Assert.Equal($"11-{DateTime.UnixEpoch.AddDays(1).Ticks}", info!.ETag);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("prefix")]
    [InlineData("padding")]
    public void Fingerprint_match_accepts_supported_formats(string format)
    {
        var pinned = format switch
        {
            "prefix" => "SHA256:" + Fingerprint,
            "padding" => "SHA256:" + Fingerprint + "=",
            _ => Fingerprint,
        };

        Assert.True(SftpObjectStoreClient.FingerprintMatches(pinned, Fingerprint));
    }

    [Fact]
    public void Fingerprint_mismatch_is_rejected()
    {
        var other = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("other host key")));
        Assert.False(SftpObjectStoreClient.FingerprintMatches(Fingerprint, other));
    }

    [Fact]
    public void Requires_exactly_one_authentication_method()
    {
        var settings = Settings();
        Assert.Throws<ArgumentException>(() => new SftpObjectStoreClient(settings));
        Assert.Throws<ArgumentException>(() => new SftpObjectStoreClient(settings, password: "password", privateKeyPem: "key"));
    }

    [Fact]
    public void Settings_string_contains_only_host_port_and_user()
    {
        var text = Settings().ToString();

        Assert.Equal("sftp.example.test:22 (reader)", text);
        Assert.DoesNotContain(Fingerprint, text);
        Assert.DoesNotContain("exports", text);
    }

    [Fact]
    public async Task Host_key_mismatch_is_reported_with_a_safe_fixed_message()
    {
        var session = new FakeSession { FailHostKey = true };
        var client = Client(session);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListAsync("", null, 1));

        Assert.Equal("The SFTP server's host key does not match the pinned fingerprint.", error.Message);
    }

    private static SftpObjectStoreClient Client(FakeSession session) => new(Settings(), session);

    private static SftpSettings Settings() => new("sftp.example.test", 22, "reader", Fingerprint, "/exports");

    private static SftpEntry Entry(string name, bool regular = false, bool directory = false, bool symlink = false) =>
        new(name, "", 9, DateTime.UnixEpoch.AddDays(1), directory, symlink, regular);

    private sealed class FakeSession : ISftpSession
    {
        private readonly Dictionary<string, List<SftpEntry>> _directories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SftpEntry> _attributes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);

        public bool IsConnected { get; private set; }
        public bool HostKeyMismatch => FailHostKey;
        public bool FailHostKey { get; init; }
        public bool Disposed { get; private set; }
        public int ConnectCount { get; private set; }
        public Func<string, bool>? FingerprintValidator { get; set; }
        public List<string> Listed { get; } = [];
        public List<string> Opened { get; } = [];

        public void AddDirectory(string path, params SftpEntry[] entries)
        {
            _directories[path] = entries.ToList();
            foreach (var entry in entries)
            {
                var fullPath = path.TrimEnd('/') + "/" + entry.Name;
                _attributes[fullPath] = entry;
            }
        }

        public void AddFile(string path, string content)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            var bytes = Encoding.UTF8.GetBytes(content);
            var entry = new SftpEntry(name, "", bytes.Length, DateTime.UnixEpoch.AddDays(1), false, false, true);
            _attributes[path] = entry;
            _contents[path] = bytes;
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            if (FailHostKey)
                throw new SshException("untrusted host");
            IsConnected = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<SftpEntry> ListDirectoryAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Listed.Add(path);
            if (!_directories.TryGetValue(path, out var entries))
                throw new SftpPathNotFoundException(path);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
                await Task.Yield();
            }
        }

        public Task<SftpEntry> GetAttributesAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _attributes.TryGetValue(path, out var entry)
                ? Task.FromResult(entry)
                : Task.FromException<SftpEntry>(new SftpPathNotFoundException(path));
        }

        public Stream OpenRead(string path)
        {
            Opened.Add(path);
            return _contents.TryGetValue(path, out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : throw new SftpPathNotFoundException(path);
        }

        public void Dispose()
        {
            Disposed = true;
            IsConnected = false;
        }
    }
}
