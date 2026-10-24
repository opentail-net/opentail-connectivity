namespace OpenTail.Connectivity.Files.Extraction;

/// <summary>Limits and policy for reading one attachment's text for an AI agent.</summary>
public sealed record AttachmentProcessingOptions
{
    /// <summary>Characters returned across all segments (<c>ExtractionOptions.MaxCharacters</c>).</summary>
    public int MaxCharacters { get; init; } = 40_000;
    /// <summary>Pages, slides or chapters read.</summary>
    public int MaxPages { get; init; } = 50;
    /// <summary>Input size; the fetcher normally refuses larger files first.</summary>
    public long MaxInputBytes { get; init; } = 20L * 1024 * 1024;
    /// <summary>Bytes decompressed from ZIP, EML and Office parts, shared across nesting.</summary>
    public long MaxDecompressedBytes { get; init; } = 80L * 1024 * 1024;
    /// <summary>Container entries, shared across nesting.</summary>
    public int MaxContainerEntries { get; init; } = 100;
    /// <summary>Parsing time; checked by the parsers between pages, rows and entries.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Images OCR'd per attachment (about a second each); the rest make the result partial.</summary>
    public int MaxOcrImages { get; init; } = 5;
    /// <summary>
    /// Return the text of attachments with Medium findings aimed at the model (obfuscated or OCR'd instructions, concealment).
    /// Off by default: such text is held back for a human. Fraud signals never hold text back.
    /// </summary>
    public bool ReturnReviewRequired { get; init; }

    public void Validate()
    {
        if (MaxCharacters is <= 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaxCharacters), "must be 1 to 1,000,000");
        if (MaxPages <= 0) throw new ArgumentOutOfRangeException(nameof(MaxPages), "must be > 0");
        if (MaxInputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxInputBytes), "must be > 0");
        if (MaxDecompressedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxDecompressedBytes), "must be > 0");
        if (MaxContainerEntries <= 0) throw new ArgumentOutOfRangeException(nameof(MaxContainerEntries), "must be > 0");
        if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout), "must be > 0");
        if (MaxOcrImages < 0) throw new ArgumentOutOfRangeException(nameof(MaxOcrImages), "must be >= 0");
    }
}

public enum AttachmentTextStatus
{
    Extracted, NoText, Binary, NeedsOcr, UnsupportedType, Protected, Malformed, TooLarge, LimitExceeded,
    ExtractionFailed, TimedOut, Withheld, HeldForReview, Busy
}

/// <summary>A piece of attachment text with where it came from. <see cref="Source"/> is recorded by the extractors, not by the text.</summary>
public sealed record AttachmentSegment(string Source, string? Location, bool Ocr, string Text, IReadOnlyList<string> FindingCodes);

/// <summary>
/// An attachment's text for an agent. <see cref="Segments"/> is empty for every status but
/// <see cref="AttachmentTextStatus.Extracted"/>; <see cref="Reason"/> says in one sentence what happened. Never holds withheld text,
/// finding excerpts or extractor messages, which can quote the document.
/// </summary>
public sealed record AttachmentText(
    AttachmentTextStatus Status,
    IReadOnlyList<AttachmentSegment> Segments,
    bool Partial,
    string Verdict,
    int RiskScore,
    IReadOnlyList<string> FindingCodes,
    IReadOnlyList<string> FraudSignals,
    IReadOnlyList<string> WithheldParts,
    string DetectedType,
    bool TypeMismatch,
    string Sha256,
    IReadOnlyList<string> Warnings,
    string? Reason);

/// <summary>Reads a document for a model. The seam for tests; the default calls <c>TextExtractor.GetModelInput</c>.</summary>
internal interface IAttachmentTextReader
{
    TT.ModelInput Read(string fileName, byte[] content, TT.ExtractionOptions options);
}

/// <summary>
/// Turns attachment bytes into text an agent may read, through <c>Transformations.Text</c>'s AI-ingestion profile: hidden text
/// dropped, High findings withheld (whole file, or the ZIP entry / email part), images OCR'd when an engine is given. Model-aimed
/// Medium findings hold the text back unless <see cref="AttachmentProcessingOptions.ReturnReviewRequired"/>; fraud signals are
/// returned with the text so the agent can warn the user. Extractions run at most two at a time per process.
/// </summary>
public sealed class AttachmentProcessor
{
    private static readonly SemaphoreSlim SharedSlots = new(2, 2);
    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(1);

    private readonly AttachmentProcessingOptions _options;
    private readonly TT.IOcrEngine? _ocr;
    private readonly IAttachmentTextReader _reader;
    private readonly SemaphoreSlim _slots;

    /// <param name="ocr">The process's shared OCR engine (see <see cref="ConnectivityOcr"/>), or null for no OCR.</param>
    public AttachmentProcessor(AttachmentProcessingOptions? options = null, TT.IOcrEngine? ocr = null)
        : this(options, ocr, new ModelInputReader(), SharedSlots) { }

    internal AttachmentProcessor(AttachmentProcessingOptions? options, TT.IOcrEngine? ocr, IAttachmentTextReader reader, SemaphoreSlim slots)
    {
        _options = options ?? new AttachmentProcessingOptions();
        _options.Validate();
        _ocr = ocr;
        _reader = reader;
        _slots = slots;
    }

    public async Task<AttachmentText> ProcessAsync(AttachmentContent content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content.Bytes));
        var detected = TT.TextExtractor.DetectFormat(content.Bytes) ?? Path.GetExtension(content.Name).ToLowerInvariant();

        if (!await _slots.WaitAsync(SlotWait, ct))
            return Empty(AttachmentTextStatus.Busy, detected, sha256, "Too many attachments are being read right now; try again shortly.");

        TT.ModelInput input;
        try
        {
            var options = TT.ExtractionOptions.ForAiIngestion() with
            {
                MaxCharacters = _options.MaxCharacters,
                MaxPages = _options.MaxPages,
                MaxInputBytes = _options.MaxInputBytes,
                MaxDecompressedBytes = _options.MaxDecompressedBytes,
                MaxContainerEntries = _options.MaxContainerEntries,
                Timeout = _options.Timeout,
                MaxOcrImages = _options.MaxOcrImages,
                OcrEngine = _ocr,
                CancellationToken = ct,
            };
            input = await Task.Run(() => _reader.Read(content.Name, content.Bytes, options), ct);
        }
        finally
        {
            _slots.Release();
        }

        return Map(input, detected, sha256, _options.ReturnReviewRequired);
    }

    internal static AttachmentText Map(TT.ModelInput input, string detectedType, string sha256, bool returnReviewRequired)
    {
        var warnings = input.Warnings.Select(w => $"{w.Code}: {w.Source}").Distinct().ToList();
        var typeMismatch = input.Warnings.Any(w => w.Code == TT.ExtractionWarningCodes.ContentTypeMismatch);
        var findingCodes = input.Findings.Select(f => f.Code).Distinct().ToList();
        var fraud = input.Findings.Where(f => f.Category == TT.SafetyCategory.Fraud && !f.Withheld).Select(f => f.Code).Distinct().ToList();

        AttachmentText Result(AttachmentTextStatus status, string? reason, IReadOnlyList<AttachmentSegment>? segments = null) => new(
            status, segments ?? [], input.IsPartial, input.Verdict.ToString(), input.RiskScore, findingCodes, fraud,
            input.WithheldSources, detectedType, typeMismatch, sha256, warnings, reason);

        switch (input.ErrorKind)
        {
            case TT.ExtractionErrorKind.None:
                break;
            case TT.ExtractionErrorKind.Empty:
                return Result(AttachmentTextStatus.NoText, "The file is empty.");
            case TT.ExtractionErrorKind.Unsupported:
                return Result(AttachmentTextStatus.UnsupportedType, "There is no reader for this file type.");
            case TT.ExtractionErrorKind.Binary:
                return Result(AttachmentTextStatus.Binary, "This is a binary file, not a document; it has no text to read.");
            case TT.ExtractionErrorKind.NeedsOcr:
                return Result(AttachmentTextStatus.NeedsOcr, "This is an image; reading text in images (OCR) is not available here.");
            case TT.ExtractionErrorKind.TooLarge:
                return Result(AttachmentTextStatus.TooLarge, "The file is larger than the limit for reading attachments.");
            case TT.ExtractionErrorKind.LimitExceeded:
                return Result(AttachmentTextStatus.LimitExceeded,
                    "The file expands past the size or part limit (an archive or document that unpacks too large, or is nested too deeply).");
            case TT.ExtractionErrorKind.TimedOut:
                return Result(AttachmentTextStatus.TimedOut, "Reading the file took longer than the time limit.");
            case TT.ExtractionErrorKind.Quarantined:
                return Result(AttachmentTextStatus.Withheld,
                    "The file was withheld by the safety check (content aimed at manipulating an AI reader). A person should open it.");
            case TT.ExtractionErrorKind.PasswordProtected:
                return Result(AttachmentTextStatus.Protected, "The file may be password-protected or encrypted.");
            case TT.ExtractionErrorKind.Corrupted:
                return Result(AttachmentTextStatus.Malformed, "The file is damaged or is not what its type says.");
            default:
                return Result(AttachmentTextStatus.ExtractionFailed, "The file could not be read.");
        }

        if (!returnReviewRequired && HeldForModelAimedFindings(input))
            return Result(AttachmentTextStatus.HeldForReview,
                "The file contains wording that may be aimed at manipulating an AI reader; its text is held back. A person should open it.");

        var segments = input.Segments
            .Select(s => new AttachmentSegment(s.Source, s.Location, s.Origin == TT.ContentOrigin.Ocr, s.Text,
                s.Findings.Select(f => f.Code).Distinct().ToList()))
            .ToList();
        if (segments.Count == 0)
            return Result(AttachmentTextStatus.NoText, input.Warnings.Any(w => w.Code == TT.ExtractionWarningCodes.NeedsOcr)
                ? "The file has no text layer (it is probably a scan or images), and OCR did not read it."
                : "The file has no readable text.");

        string? note = null;
        if (fraud.Count > 0)
            note = "The file shows signs of a scam aimed at the reader (see fraudSignals). Tell the user before acting on it.";
        else if (input.WithheldSources.Count > 0)
            note = "Some parts were withheld by the safety check (see withheldParts).";
        else if (input.IsPartial)
            note = "The text is incomplete (truncated, or some parts or images were not read).";
        return Result(AttachmentTextStatus.Extracted, note, segments);
    }

    /// <summary>
    /// A Medium or higher finding aimed at the model in the text that would be returned, or a review verdict not explained by
    /// fraud alone. The verdict is not enough by itself: with a withheld ZIP entry it is PartsWithheld even when another entry
    /// that is returned has a Medium finding.
    /// </summary>
    private static bool HeldForModelAimedFindings(TT.ModelInput input)
    {
        static bool ModelAimed(TT.SafetyFinding f) => f.Severity >= TT.SafetySeverity.Medium && f.Category != TT.SafetyCategory.Fraud;
        if (input.Segments.SelectMany(s => s.Findings).Any(ModelAimed)) return true;
        return input.Verdict == TT.SafetyVerdict.ReviewRequired && input.Findings.Where(f => !f.Withheld).Any(ModelAimed);
    }

    private static AttachmentText Empty(AttachmentTextStatus status, string detectedType, string sha256, string reason) =>
        new(status, [], false, "", 0, [], [], [], detectedType, false, sha256, [], reason);

    private sealed class ModelInputReader : IAttachmentTextReader
    {
        private static readonly TT.TextExtractor Extractor = new();

        public TT.ModelInput Read(string fileName, byte[] content, TT.ExtractionOptions options) =>
            Extractor.GetModelInput(fileName, content, options);
    }
}

/// <summary>The process's OCR engine: one Tesseract engine, shared across threads (the library pools it), disposed at shutdown.</summary>
public static class ConnectivityOcr
{
    /// <summary>
    /// An engine for the tessdata folder, or null when none is configured. The engine may still be unavailable (not Windows
    /// x64/x86, missing language data); then images are reported as needing OCR, and <paramref name="unavailableReason"/> says why.
    /// </summary>
    public static Transformations.Text.Ocr.TesseractOcrEngine? Create(string? tessdataPath, string languages, out string? unavailableReason)
    {
        unavailableReason = null;
        if (string.IsNullOrWhiteSpace(tessdataPath))
        {
            unavailableReason = "No tessdata folder is configured.";
            return null;
        }
        var engine = new Transformations.Text.Ocr.TesseractOcrEngine(tessdataPath, string.IsNullOrWhiteSpace(languages) ? "eng" : languages);
        unavailableReason = engine.UnavailableReason;
        return engine;
    }
}
