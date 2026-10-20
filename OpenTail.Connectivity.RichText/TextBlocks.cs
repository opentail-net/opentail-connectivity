using System.Text;

namespace OpenTail.Connectivity.RichText;

/// <summary>
/// Collects plain text block by block: one blank line between blocks, prefixes for list items and quotes, and no runs of blank
/// lines. Keeps the simple structure (headings, lists, code fences) that chunking and models read well.
/// </summary>
internal sealed class TextBlocks
{
    private readonly StringBuilder text = new();
    private readonly List<LinePrefix> prefixes = [];
    private readonly StringBuilder line = new();
    private bool pendingBlankLine;

    /// <summary>Appends inline text to the current line.</summary>
    public void Inline(string? s)
    {
        if (string.IsNullOrEmpty(s)) return;
        line.Append(line.Length == 0 ? s.TrimStart(' ') : s);
    }

    /// <summary>Ends the current line (a hard break inside a block).</summary>
    public void Break() => FlushLine(keepEmpty: true);

    /// <summary>Ends the current block; the next text starts after a blank line.</summary>
    public void EndBlock()
    {
        FlushLine(keepEmpty: false);
        // Inside a list item or quote, blocks follow each other without blank lines; the list or quote as a whole is one block.
        if (text.Length > 0 && prefixes.Count == 0) pendingBlankLine = true;
    }

    /// <summary>A whole line of its own, such as a heading or a code line, with the current prefixes.</summary>
    public void Line(string s)
    {
        FlushLine(keepEmpty: false);
        line.Append(s);
        FlushLine(keepEmpty: true);
    }

    /// <summary>
    /// Prefixes the lines written until disposed: the first line gets <paramref name="first"/> (a list marker), later ones
    /// <paramref name="rest"/> (the indent under it). Prefixes nest.
    /// </summary>
    public IDisposable Prefix(string first, string rest)
    {
        FlushLine(keepEmpty: false);
        var p = new LinePrefix(first, rest);
        prefixes.Add(p);
        return new Pop(this, p);
    }

    public override string ToString()
    {
        FlushLine(keepEmpty: false);
        return text.ToString().TrimEnd();
    }

    private void FlushLine(bool keepEmpty)
    {
        var content = line.ToString().TrimEnd();
        line.Clear();
        if (content.Length == 0 && (!keepEmpty || text.Length == 0)) return;
        if (pendingBlankLine) text.Append('\n');
        pendingBlankLine = false;

        foreach (var p in prefixes)
        {
            text.Append(p.Used ? p.Rest : p.First);
            p.Used = true;
        }
        text.Append(content).Append('\n');
    }

    private sealed class LinePrefix(string first, string rest)
    {
        public string First { get; } = first;
        public string Rest { get; } = rest;
        public bool Used { get; set; }
    }

    private sealed class Pop(TextBlocks owner, LinePrefix prefix) : IDisposable
    {
        public void Dispose()
        {
            owner.FlushLine(keepEmpty: false);
            owner.prefixes.Remove(prefix);
        }
    }
}
