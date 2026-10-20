using OpenTail.Connectivity.Email;
using OpenTail.Connectivity.Files.Conformance;
using OpenTail.Connectivity.Files.Contracts;

namespace OpenTail.Connectivity.Tests.Conformance;

public sealed class EmailConnectorsConformanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otail-conf-email-" + Guid.NewGuid().ToString("N"));
    private readonly string _emptyDir = Path.Combine(Path.GetTempPath(), "otail-conf-email-empty-" + Guid.NewGuid().ToString("N"));
    private readonly string _mboxFile;
    private readonly string _emptyMboxFile;

    public EmailConnectorsConformanceTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_emptyDir);

        File.WriteAllText(Path.Combine(_dir, "msg1.eml"), "From: alice@example.com\nSubject: Test 1\nDate: Mon, 01 Jan 2026 00:00:00 +0000\n\nBody one.\n");
        File.WriteAllText(Path.Combine(_dir, "msg2.eml"), "From: bob@example.com\nSubject: Test 2\nDate: Mon, 01 Jan 2026 00:00:00 +0000\n\nBody two.\n");

        _mboxFile = Path.Combine(_dir, "messages.mbox");
        File.WriteAllText(_mboxFile, """
            From a@example.com Thu Jan  1 00:00:00 2026
            From: a@example.com
            Subject: hi 1
            Message-ID: <msg1@example.com>
            Date: Mon, 01 Jan 2026 00:00:00 +0000

            body 1

            From a@example.com Thu Jan  1 01:00:00 2026
            From: a@example.com
            Subject: hi 2
            Message-ID: <msg2@example.com>
            Date: Mon, 01 Jan 2026 01:00:00 +0000

            body 2

            """);

        _emptyMboxFile = Path.Combine(_emptyDir, "empty.mbox");
        File.WriteAllText(_emptyMboxFile, "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_emptyDir, recursive: true); } catch (IOException) { }
    }

    private sealed class EmlFixture(string dir, string emptyDir) : IConnectorFixture
    {
        public IReadOnlyList<string> SecretsGiven => [];
        public ISourceConnector Create() => new EmlFileConnector(dir, "tenant-eml");
        public ISourceConnector CreateEmpty() => new EmlFileConnector(emptyDir, "tenant-eml");
        public Task RemoveOneItemAsync(string sourceRef)
        {
            if (File.Exists(sourceRef)) File.Delete(sourceRef);
            return Task.CompletedTask;
        }
    }

    private sealed class MboxFixture(string mboxFile, string emptyMboxFile) : IConnectorFixture
    {
        public IReadOnlyList<string> SecretsGiven => [];
        public ISourceConnector Create() => new MboxConnector(mboxFile, "tenant-mbox");
        public ISourceConnector CreateEmpty() => new MboxConnector(emptyMboxFile, "tenant-mbox");
        public Task RemoveOneItemAsync(string sourceRef)
        {
            File.WriteAllText(mboxFile, """
                From a@example.com Thu Jan  1 01:00:00 2026
                From: a@example.com
                Subject: hi 2
                Message-ID: <msg2@example.com>
                Date: Mon, 01 Jan 2026 01:00:00 +0000

                body 2

                """);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task EmlFileConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new EmlFixture(_dir, _emptyDir));
    }

    [Fact]
    public async Task MboxConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new MboxFixture(_mboxFile, _emptyMboxFile));
    }
}
