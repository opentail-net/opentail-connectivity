namespace OpenTail.Connectivity.Tests;

public sealed class EmlTextExtractorTests
{
    private readonly EmlTextExtractor _extractor = new();

    private static byte[] BuildEml(string subject, string from, string to, string body, bool html = false)
    {
        var msg = new MimeMessage();
        msg.Subject = subject;
        msg.From.Add(MailboxAddress.Parse(from));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Date = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

        if (html)
            msg.Body = new TextPart("html") { Text = $"<html><body>{body}</body></html>" };
        else
            msg.Body = new TextPart("plain") { Text = body };

        using var ms = new MemoryStream();
        msg.WriteTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Extracts_plain_text_body()
    {
        var bytes = BuildEml("Hello", "alice@example.com", "bob@example.com", "Plain body content here.");
        var result = _extractor.Extract("msg.eml", bytes);
        Assert.True(result.Success);
        Assert.Contains("Plain body content here.", result.Text);
    }

    [Fact]
    public void Includes_subject_in_text()
    {
        var bytes = BuildEml("Meeting tomorrow", "a@b.com", "c@d.com", "See you then.");
        var result = _extractor.Extract("msg.eml", bytes);
        Assert.Contains("Meeting tomorrow", result.Text);
    }

    [Fact]
    public void Includes_from_in_text()
    {
        var bytes = BuildEml("Sub", "alice@example.com", "bob@example.com", "Body.");
        var result = _extractor.Extract("msg.eml", bytes);
        Assert.Contains("alice@example.com", result.Text);
    }

    [Fact]
    public void Metadata_contains_subject_and_from()
    {
        var bytes = BuildEml("Subject A", "alice@example.com", "bob@example.com", "Body.");
        var result = _extractor.Extract("msg.eml", bytes);
        Assert.Equal("Subject A", result.Metadata!["email.subject"]);
        Assert.Contains("alice@example.com", result.Metadata["email.from"]);
    }

    [Fact]
    public void Falls_back_to_stripped_html_when_no_plain_text()
    {
        var bytes = BuildEml("HTML email", "a@b.com", "c@d.com", "<b>Bold</b> content", html: true);
        var result = _extractor.Extract("msg.eml", bytes);
        Assert.True(result.Success);
        Assert.Contains("Bold", result.Text);
        Assert.DoesNotContain("<b>", result.Text);
    }

    [Fact]
    public void Returns_failure_for_corrupt_bytes()
    {
        var result = _extractor.Extract("bad.eml", "not an eml"u8.ToArray());
        Assert.False(result.Success);
    }
}
