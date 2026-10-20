using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public sealed class FilesMemoryBackendTests
{
    [Fact]
    public void RefusesAFolderThatDoesNotExist()
        => Assert.Throws<DirectoryNotFoundException>(() => new FilesMemoryBackend(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")));

    [Fact]
    public void MarkdownIsReadableAndRoundTrips()
    {
        var observed = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var record = new MemoryRecord(
            "acme/owner:1",
            MemoryRecordKind.DecisionLog,
            new MemorySource(MemorySourceType.UserNote, "notes", Writer: "person:ops-lead", Acl: new MemorySourceAcl(true, "restricted", ["group:ops"])),
            new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable, PermissionScope: "ops", Tainted: true),
            "Priya Shah owns the Acme account.\n\nSecond paragraph, with: a colon.",
            observed,
            observed,
            Labels: ["acme"]);

        var markdown = FilesMemoryBackend.ToMarkdown(record);
        var back = FilesMemoryBackend.FromMarkdown(markdown);

        Assert.StartsWith("---\nId: \"acme/owner:1\"\n", markdown, StringComparison.Ordinal);
        Assert.Contains("Policy.TrustLevel: \"High\"", markdown, StringComparison.Ordinal);
        Assert.Contains("Policy.Tainted: true", markdown, StringComparison.Ordinal);
        Assert.Contains("Source.Acl.Principals: [\"group:ops\"]", markdown, StringComparison.Ordinal);
        Assert.EndsWith("---\nPriya Shah owns the Acme account.\n\nSecond paragraph, with: a colon.\n", markdown, StringComparison.Ordinal);
        Assert.Equal(record.Content, back.Content);
        Assert.Equal(record.Policy with { RequiredClaims = null }, back.Policy with { RequiredClaims = null });
        Assert.Equal(["group:ops"], back.Source.Acl!.Principals!);
        Assert.Equal(["acme"], back.Labels!);
    }

    [Fact]
    public async Task AnEditedFileIsWhatSearchSees()
    {
        var root = Directory.CreateTempSubdirectory("ot-files-edit-").FullName;
        var backend = new FilesMemoryBackend(root);
        var observed = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        await backend.UpsertAsync(new MemoryRecord("note", MemoryRecordKind.DecisionLog, new MemorySource(MemorySourceType.UserNote, "notes", Acl: MemorySourceAcl.OrganisationWide),
            new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.Medium, MemoryRetentionClass.Durable), "Standup is at ten.", observed, observed), []);

        var path = backend.PathOf("note");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("Standup is at ten.", "Standup moved to eleven.", StringComparison.Ordinal));

        var hit = Assert.Single(await backend.SearchAsync(new MemorySearch("standup eleven", MemorySearchKinds.Keyword, 5, new MemoryFilter())));
        Assert.Equal("Standup moved to eleven.", hit.Text);
    }
}
