namespace OpenTail.Connectivity.Tests;

/// <summary>The EML connector's folder is a boundary, as the folder connector's is: no links, no paths outside, no other tenant.</summary>
public sealed class EmlFileBoundaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eml-boundary-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside;
    private const string Eml = "From: a@example.com\r\nSubject: inside\r\nMessage-ID: <in@example.com>\r\n\r\nbody\r\n";

    public EmlFileBoundaryTests()
    {
        _outside = _dir + "-outside";
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_dir, "in.eml"), Eml);
        File.WriteAllText(Path.Combine(_outside, "secret.eml"), Eml.Replace("inside", "secret"));
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private static SourceItem Item(string path, string tenant = "t") => new(path, "x", "eml", 1, DateTimeOffset.UtcNow, tenant);

    [Fact]
    public async Task A_forged_item_outside_the_folder_is_refused()
    {
        var connector = new EmlFileConnector(_dir, "t");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.FetchAsync(Item(Path.Combine(_outside, "secret.eml"))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.FetchAsync(Item(Path.Combine(_dir, "..", Path.GetFileName(_outside), "secret.eml"))));
    }

    [Fact]
    public async Task An_item_for_another_tenant_is_refused()
    {
        var connector = new EmlFileConnector(_dir, "t");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.FetchAsync(Item(Path.Combine(_dir, "in.eml"), tenant: "other")));
        Assert.NotEmpty(await connector.FetchAsync(Item(Path.Combine(_dir, "in.eml"))));
    }

    [Fact]
    public async Task A_linked_file_is_neither_discovered_nor_fetched()
    {
        var link = Path.Combine(_dir, "link.eml");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(_outside, "secret.eml"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("This machine cannot create symbolic links.");
        }

        var connector = new EmlFileConnector(_dir, "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync(TestContext.Current.CancellationToken)) items.Add(item);

        Assert.Equal(["inside"], items.Select(i => i.DisplayName));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.FetchAsync(Item(link)));
    }
}
