using OpenTail.Connectivity.Files;

namespace OpenTail.Connectivity.Tests;

/// <summary>Pins the shared file rules, so the folder, drive and bucket connectors all mean the same thing by them.</summary>
public sealed class FileFilterTests
{
    [Theory]
    [InlineData("**/*.md", "readme.md", true)]           // ** matches zero segments
    [InlineData("**/*.md", "docs/a/b/readme.md", true)]
    [InlineData("*.md", "docs/readme.md", false)]         // * stays in one segment
    [InlineData("docs/*.md", "docs/readme.md", true)]
    [InlineData("docs/*.md", "docs/sub/readme.md", false)]
    [InlineData("**/*.MD", "Docs/README.md", true)]       // case-insensitive
    [InlineData("docs/**", "docs/a/b.txt", true)]
    [InlineData("docs/*.md", @"docs\readme.md", true)]    // backslash is a separator
    public void Include_patterns(string pattern, string path, bool included) =>
        Assert.Equal(included, new FileFilter(new FileFilterOptions(IncludePatterns: [pattern])).MatchesPatterns(path));

    [Fact]
    public void Exclude_wins_over_include()
    {
        var filter = new FileFilter(new FileFilterOptions(IncludePatterns: ["**/*.cs"], ExcludePatterns: ["**/*.generated.cs"]));
        Assert.True(filter.MatchesPatterns("src/a.cs"));
        Assert.False(filter.MatchesPatterns("src/a.generated.cs"));
        Assert.False(filter.MatchesPatterns("src/a.md"));
    }

    [Fact]
    public void Default_extensions_replaced_or_extended()
    {
        var defaults = new FileFilter();
        Assert.True(defaults.HasSupportedExtension("a/b.PDF"));
        Assert.False(defaults.HasSupportedExtension("a/b.log"));
        Assert.False(defaults.HasSupportedExtension("Makefile"));

        Assert.True(new FileFilter(new FileFilterOptions(ExtraExtensions: ["log"])).HasSupportedExtension("x.log"));
        var only = new FileFilter(new FileFilterOptions(SupportedExtensions: [".log"]));
        Assert.True(only.HasSupportedExtension("x.log"));
        Assert.False(only.HasSupportedExtension("x.pdf"));
    }

    [Fact]
    public void The_extension_is_taken_from_the_last_segment()
    {
        // "v1.2/notes" has a dot in a folder name, not in the file name.
        Assert.False(new FileFilter().HasSupportedExtension("v1.2/notes"));
        Assert.True(new FileFilter().HasSupportedExtension(@"v1.2\notes.txt"));
    }

    [Fact]
    public void Size_cap_and_everything_together()
    {
        var filter = new FileFilter(new FileFilterOptions(IncludePatterns: ["reports/**"], MaxFileSizeBytes: 100));
        Assert.True(filter.Includes("reports/q3.pdf", 100));
        Assert.False(filter.Includes("reports/q3.pdf", 101));
        Assert.False(filter.Includes("other/q3.pdf", 1));
        Assert.False(filter.Includes("reports/q3.exe", 1));
    }

    [Fact]
    public void The_folder_options_map_onto_the_filter()
    {
        var options = FolderSourceConnector.Filter(new FolderConnectorOptions(IncludePatterns: ["a/**"], MaxFileSizeBytes: 5, ExtraExtensions: [".log"]));
        Assert.Equal(["a/**"], options.IncludePatterns);
        Assert.Equal(5, options.MaxFileSizeBytes);
        Assert.Equal([".log"], options.ExtraExtensions);
    }
}
