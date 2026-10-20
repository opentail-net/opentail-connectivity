using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using OpenTail.Connectivity.Files.Contracts;

namespace OpenTail.Connectivity.RichText;

/// <summary>
/// Confluence storage format (the XHTML with <c>ac:</c> and <c>ri:</c> elements that Confluence pages are stored in) to plain
/// text. Keeps headings, lists, tables, code macros and panel text; links keep their text, user links become <c>@user</c>, and
/// images and attachments become a marker. Macros it does not know keep the text of their body. If the markup does not parse,
/// the tags are stripped instead, so a page never fails to read.
/// </summary>
public sealed partial class ConfluenceStorageConverter : ITextExtractor
{
    private static readonly XNamespace Ac = "http://www.atlassian.com/schema/confluence/4/ac/";
    private static readonly XNamespace Ri = "http://www.atlassian.com/schema/confluence/4/ri/";

    private static readonly HashSet<string> XmlEntities = ["lt", "gt", "amp", "quot", "apos"];

    /// <summary>Macros whose output is navigation or decoration, not content.</summary>
    private static readonly HashSet<string> SkippedMacros = new(StringComparer.OrdinalIgnoreCase)
    {
        "toc", "children", "pagetree", "recently-updated", "contentbylabel", "livesearch", "anchor", "space-details",
    };

    public static string ToText(string storageFormat)
    {
        if (string.IsNullOrWhiteSpace(storageFormat)) return "";
        XElement root;
        try
        {
            root = XElement.Parse(
                $"<root xmlns:ac=\"{Ac.NamespaceName}\" xmlns:ri=\"{Ri.NamespaceName}\">{NamedEntities(storageFormat)}</root>",
                LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return StripTags(storageFormat);
        }

        var output = new TextBlocks();
        Nodes(root, output);
        return output.ToString();
    }

    public TextExtractionResult Extract(string fileName, byte[] content) => new(true, ToText(Encoding.UTF8.GetString(content)));

    private static void Nodes(XElement parent, TextBlocks output)
    {
        foreach (var node in parent.Nodes()) Node(node, output);
    }

    private static void Node(XNode node, TextBlocks output)
    {
        switch (node)
        {
            case XText t:
                output.Inline(Collapse(t.Value));
                return;
            case XElement e:
                Element(e, output);
                return;
        }
    }

    private static void Element(XElement e, TextBlocks output)
    {
        var name = e.Name.Namespace == XNamespace.None ? e.Name.LocalName.ToLowerInvariant() : e.Name.LocalName;

        if (e.Name.Namespace == Ac)
        {
            AcElement(e, name, output);
            return;
        }
        if (e.Name.Namespace == Ri)
        {
            return; // resource identifiers are read by their parent link or image
        }

        switch (name)
        {
            case "p":
            case "div":
            case "section":
                Nodes(e, output);
                output.EndBlock();
                break;
            case "br":
                output.Break();
                break;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                output.Line(new string('#', name[1] - '0') + " " + InlineText(e));
                output.EndBlock();
                break;
            case "ul":
            case "ol":
                var n = 1;
                foreach (var li in e.Elements().Where(x => x.Name.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase)))
                {
                    var marker = name == "ol" ? $"{n++}. " : "- ";
                    using (output.Prefix(marker, new string(' ', marker.Length))) ListItem(li, output);
                }
                output.EndBlock();
                break;
            case "blockquote":
                using (output.Prefix("> ", "> ")) Nodes(e, output);
                output.EndBlock();
                break;
            case "pre":
                Code(null, e.Value, output);
                break;
            case "hr":
                output.Line("---");
                output.EndBlock();
                break;
            case "table":
                foreach (var row in e.Descendants().Where(x => x.Name.LocalName.Equals("tr", StringComparison.OrdinalIgnoreCase)))
                {
                    var cells = row.Elements().Where(c => c.Name.LocalName is "td" or "th" or "TD" or "TH").Select(c => Cell(c));
                    output.Line("| " + string.Join(" | ", cells) + " |");
                }
                output.EndBlock();
                break;
            case "a":
                var text = InlineText(e);
                var href = (string?)e.Attribute("href");
                output.Inline(href is { Length: > 0 } && href != text ? $"{text} ({href})" : text);
                break;
            case "time":
                output.Inline((string?)e.Attribute("datetime") ?? e.Value);
                break;
            case "code":
                output.Inline("`" + e.Value + "`");
                break;
            case "img":
                output.Inline("[image" + ((string?)e.Attribute("alt") is { Length: > 0 } alt ? ": " + alt : "") + "]");
                break;
            default:
                Nodes(e, output);
                break;
        }
    }

    private static void AcElement(XElement e, string name, TextBlocks output)
    {
        switch (name)
        {
            case "structured-macro":
            case "macro":
                Macro(e, output);
                break;
            case "link":
                output.Inline(LinkText(e));
                break;
            case "image":
                var file = (string?)e.Element(Ri + "attachment")?.Attribute(Ri + "filename") ?? (string?)e.Element(Ri + "url")?.Attribute(Ri + "value");
                output.Inline("[image" + (file is { Length: > 0 } ? ": " + file : "") + "]");
                break;
            case "emoticon":
                output.Inline((string?)e.Attribute(Ac + "emoji-fallback") ?? ":" + ((string?)e.Attribute(Ac + "name") ?? "emoticon") + ":");
                break;
            case "task-list":
                foreach (var task in e.Elements(Ac + "task"))
                {
                    var done = string.Equals(task.Element(Ac + "task-status")?.Value, "complete", StringComparison.OrdinalIgnoreCase);
                    var marker = done ? "[x] " : "[ ] ";
                    using (output.Prefix(marker, "    "))
                        if (task.Element(Ac + "task-body") is { } body) Nodes(body, output);
                }
                output.EndBlock();
                break;
            case "placeholder":
            case "parameter":
            case "task-id":
            case "task-status":
            case "task-uuid":
                break;
            default:
                Nodes(e, output);
                break;
        }
    }

    private static void Macro(XElement macro, TextBlocks output)
    {
        var name = ((string?)macro.Attribute(Ac + "name") ?? "").ToLowerInvariant();
        if (SkippedMacros.Contains(name)) return;

        var parameters = macro.Elements(Ac + "parameter")
            .GroupBy(p => (string?)p.Attribute(Ac + "name") ?? "")
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

        if (name is "code" or "noformat")
        {
            Code(parameters.GetValueOrDefault("language"), macro.Element(Ac + "plain-text-body")?.Value ?? "", output);
            return;
        }

        if (name is "jira")
        {
            output.Inline(parameters.GetValueOrDefault("key") ?? "[jira issues]");
            return;
        }

        if (name is "status")
        {
            output.Inline("[" + parameters.GetValueOrDefault("title") + "]");
            return;
        }

        if (name is "view-file" or "viewpdf" or "viewdoc" or "viewxls" or "viewppt")
        {
            var file = (string?)macro.Descendants(Ri + "attachment").FirstOrDefault()?.Attribute(Ri + "filename");
            output.Line("[attachment" + (file is { Length: > 0 } ? ": " + file : "") + "]");
            output.EndBlock();
            return;
        }

        // info, note, warning, tip, panel, expand, excerpt and anything unknown: keep the body's text, with the title if any.
        if (parameters.GetValueOrDefault("title") is { Length: > 0 } title)
        {
            output.Line(title);
        }
        if (macro.Element(Ac + "rich-text-body") is { } rich)
        {
            Nodes(rich, output);
            output.EndBlock();
        }
        else if (macro.Element(Ac + "plain-text-body") is { } plain)
        {
            foreach (var line in plain.Value.Split('\n')) output.Line(line.TrimEnd('\r'));
            output.EndBlock();
        }
    }

    private static void ListItem(XElement li, TextBlocks output)
    {
        foreach (var node in li.Nodes())
        {
            if (node is XElement child && child.Name.LocalName is "ul" or "ol")
                Element(child, output);
            else
                Node(node, output);
        }
    }

    private static void Code(string? language, string code, TextBlocks output)
    {
        output.Line("```" + (language ?? ""));
        foreach (var line in code.Replace("\r\n", "\n").Trim('\n').Split('\n')) output.Line(line);
        output.Line("```");
        output.EndBlock();
    }

    private static string LinkText(XElement link)
    {
        var body = link.Element(Ac + "link-body") ?? link.Element(Ac + "plain-text-link-body");
        if (body is not null && body.Value.Trim() is { Length: > 0 } text) return Collapse(text);
        if (link.Element(Ri + "user") is { } user)
            return "@" + ((string?)user.Attribute(Ri + "username") ?? (string?)user.Attribute(Ri + "account-id") ?? (string?)user.Attribute(Ri + "userkey") ?? "user");
        if (link.Element(Ri + "page") is { } page) return (string?)page.Attribute(Ri + "content-title") ?? "";
        if (link.Element(Ri + "attachment") is { } attachment) return (string?)attachment.Attribute(Ri + "filename") ?? "";
        if (link.Element(Ri + "space") is { } space) return (string?)space.Attribute(Ri + "space-key") ?? "";
        if (link.Element(Ri + "url") is { } url) return (string?)url.Attribute(Ri + "value") ?? "";
        return (string?)link.Attribute(Ac + "anchor") ?? "";
    }

    private static string InlineText(XElement e)
    {
        var inner = new TextBlocks();
        foreach (var node in e.Nodes()) Node(node, inner);
        return inner.ToString().Replace('\n', ' ');
    }

    private static string Cell(XElement cell) => InlineText(cell).Replace("|", "\\|").Trim();

    private static string Collapse(string s) => WhitespaceRun().Replace(s, " ");

    /// <summary>HTML named entities (<c>&amp;nbsp;</c>, <c>&amp;mdash;</c>) are not XML; turn them into the characters.</summary>
    private static string NamedEntities(string s) => NamedEntity().Replace(s, m =>
    {
        var name = m.Groups[1].Value;
        if (XmlEntities.Contains(name)) return m.Value;
        var decoded = WebUtility.HtmlDecode(m.Value);
        return decoded == m.Value ? "" : decoded.Replace(" ", " ");
    });

    private static string StripTags(string s)
    {
        var text = Tag().Replace(s.Replace("</p>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n"), " ");
        return string.Join('\n', WebUtility.HtmlDecode(text).Split('\n').Select(l => Collapse(l).Trim()).Where(l => l.Length > 0));
    }

    [GeneratedRegex(@"&([A-Za-z][A-Za-z0-9]*);")]
    private static partial Regex NamedEntity();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
