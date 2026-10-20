using System.Text.RegularExpressions;

namespace OpenTail.Connectivity.Files.Redaction;

/// <summary>Which patterns <see cref="PatternRedactor"/> removes. None is on by default.</summary>
/// <param name="Custom">Extra rules by name: a regular expression whose matches are replaced by <c>[REDACTED:name]</c>.</param>
public sealed record RedactionOptions(
    bool EmailAddresses = false,
    bool PhoneNumbers = false,
    bool PaymentCards = false,
    bool Ibans = false,
    IReadOnlyDictionary<string, string>? Custom = null);

/// <summary>
/// Replaces email addresses, phone numbers, payment card numbers (Luhn-checked), IBANs (checksum-checked) and custom patterns
/// with <c>[REDACTED:&lt;rule&gt;]</c> in the title, body and metadata values, and records <c>redaction.count</c>.
/// </summary>
/// <remarks>
/// Pattern matching is a safety net, not a compliance control: it misses names, addresses and free-text identifiers, and it can
/// match things that are not personal data.
/// </remarks>
public sealed partial class PatternRedactor : IDocumentTransformer
{
    private readonly List<(string Name, Regex Pattern, Func<string, bool>? Check)> _rules = [];

    public PatternRedactor(RedactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.EmailAddresses) _rules.Add(("email", Email(), null));
        if (options.PaymentCards) _rules.Add(("card", Card(), Luhn));
        if (options.Ibans) _rules.Add(("iban", Iban(), IbanChecksum));
        // Nine digits at least, so dates (2026-10-17) and short numbers are left alone; local seven-digit numbers are missed.
        if (options.PhoneNumbers) _rules.Add(("phone", Phone(), s => DigitsBetween(9, 15)(s) && !DateLike().IsMatch(s)));
        foreach (var (name, pattern) in options.Custom ?? new Dictionary<string, string>())
        {
            if (!RuleName().IsMatch(name)) throw new ArgumentException($"Redaction rule names are letters, digits, '_' and '-': '{name}'.");
            _rules.Add((name, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)), null));
        }
    }

    public NormalisedDocument Transform(NormalisedDocument document)
    {
        var count = 0;
        string Redact(string text)
        {
            foreach (var (name, pattern, check) in _rules)
                text = pattern.Replace(text, m =>
                {
                    if (check is not null && !check(m.Value)) return m.Value;
                    count++;
                    return $"[REDACTED:{name}]";
                });
            return text;
        }

        var metadata = document.Metadata.ToDictionary(kv => kv.Key, kv => Redact(kv.Value), StringComparer.OrdinalIgnoreCase);
        var title = Redact(document.Title);
        var body = Redact(document.Body);
        metadata["redaction.count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return document with { Title = title, Body = body, Metadata = metadata };
    }

    /// <summary>The Luhn checksum over the digits, which real card numbers pass and most random digit runs do not.</summary>
    internal static bool Luhn(string candidate)
    {
        var digits = candidate.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    /// <summary>The ISO 13616 mod-97 check.</summary>
    internal static bool IbanChecksum(string candidate)
    {
        var s = new string(candidate.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        if (s.Length is < 15 or > 34) return false;
        var rearranged = s[4..] + s[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = value > 9 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
        }
        return remainder == 1;
    }

    private static Func<string, bool> DigitsBetween(int min, int max) => s =>
    {
        var n = s.Count(char.IsAsciiDigit);
        return n >= min && n <= max;
    };

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:\d[ \-]?){12,18}\d\b")]
    private static partial Regex Card();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]){11,30}\b")]
    private static partial Regex Iban();

    [GeneratedRegex(@"(?<![\w])\+?\(?\d{1,4}\)?(?:[ .\-]?\(?\d{2,4}\)?){2,5}(?![\w])")]
    private static partial Regex Phone();

    [GeneratedRegex("^[A-Za-z0-9_\\-]{1,40}$")]
    private static partial Regex RuleName();

    [GeneratedRegex(@"^\d{4}[\-./]\d{1,2}[\-./]\d{1,2}$|^\d{1,2}[\-./]\d{1,2}[\-./]\d{4}$")]
    private static partial Regex DateLike();
}
