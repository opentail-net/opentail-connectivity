using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenTail.Connectivity.Files.Models;

/// <summary>
/// Calendar times as APIs send them: a wall-clock time with a separate zone (Graph; Google when no offset is given), or a bare
/// date for all-day events. Never reads the machine's zone, so a server's setting cannot move an event.
/// </summary>
/// <remarks>
/// Zones are Windows or IANA ids (<see cref="TimeZoneInfo.TryFindSystemTimeZoneById(string, out TimeZoneInfo?)"/> accepts both).
/// An unknown zone is read as UTC. A wall-clock time that a daylight-saving change skips or repeats is read with the offset in
/// force just before the change: a skipped 02:30 lands an hour later on the clock, a repeated 02:30 is the first one.
/// </remarks>
public static partial class CalendarTime
{
    /// <summary>The instant <paramref name="dateTime"/> names. An explicit offset or Z wins over <paramref name="timeZone"/>. Null when unreadable.</summary>
    public static DateTimeOffset? FromZoned(string? dateTime, string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(dateTime))
            return null;

        var text = dateTime.Trim();
        if (HasOffset().IsMatch(text))
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var explicitOffset) ? explicitOffset : null;

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wallClock))
            return null;

        wallClock = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        return Zone(timeZone) is { } zone ? InZone(wallClock, zone) : new DateTimeOffset(wallClock, TimeSpan.Zero);
    }

    /// <summary>Midnight starting <paramref name="date"/> (yyyy-MM-dd) in <paramref name="timeZone"/>. Null when unreadable.</summary>
    public static DateTimeOffset? FromDate(string? date, string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(date)
            || !DateTime.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return null;

        return FromZoned(day.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture), timeZone);
    }

    /// <summary>The UTC wall-clock text, without an offset, to send with the zone "UTC".</summary>
    public static string ToUtcWallClock(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    /// <summary>The calendar date <paramref name="value"/> falls on in <paramref name="timeZone"/> (UTC when unknown), as yyyy-MM-dd.</summary>
    public static string ToDate(DateTimeOffset value, string? timeZone)
    {
        var local = Zone(timeZone) is { } zone ? TimeZoneInfo.ConvertTime(value, zone) : value.ToUniversalTime();
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>The wall-clock text of <paramref name="value"/> in <paramref name="timeZone"/>, without an offset. Null when the zone is unknown.</summary>
    public static string? ToWallClock(DateTimeOffset value, string? timeZone)
        => Zone(timeZone) is { } zone
            ? TimeZoneInfo.ConvertTime(value, zone).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)
            : null;

    private static TimeZoneInfo? Zone(string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
            return null;
        var id = timeZone.Trim();
        if (id is "UTC" or "Etc/UTC" or "Z" or "tzone://Microsoft/Utc")
            return TimeZoneInfo.Utc;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
    }

    private static DateTimeOffset InZone(DateTime wallClock, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(wallClock))
        {
            // Skipped by a spring-forward change: read with the offset before it.
            var before = zone.GetUtcOffset(wallClock.AddDays(-1));
            return new DateTimeOffset(wallClock, before);
        }

        if (zone.IsAmbiguousTime(wallClock))
            return new DateTimeOffset(wallClock, zone.GetAmbiguousTimeOffsets(wallClock).Max()); // the first of the two

        return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex HasOffset();
}
