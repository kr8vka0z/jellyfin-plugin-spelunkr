// Segment: one stretch of a channel's schedule as described by schedule.js (a film, an ad, or a bump).
namespace Jellyfin.Plugin.Spelunkr.Schedule;

/// <summary>What plays on a channel from <see cref="StartMs"/> for <see cref="LengthMs"/> (Unix ms).</summary>
public sealed record Segment
{
    /// <summary>Gets the start, Unix ms.</summary>
    public long StartMs { get; init; }

    /// <summary>Gets the length in ms.</summary>
    public long LengthMs { get; init; }

    /// <summary>Gets the end, Unix ms.</summary>
    public long EndMs => StartMs + LengthMs;

    /// <summary>Gets the manifest version this came from.</summary>
    public int Version { get; init; }

    /// <summary>Gets the playlist cycle (negative before the version's epoch).</summary>
    public int Cycle { get; init; }

    /// <summary>Gets the position inside the cycle.</summary>
    public int Index { get; init; }

    /// <summary>Gets "show", "ad" or "bump" (anything else is a kind this plugin does not draw yet).</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets the archive.org item id (shows), slice id (ads) or bump ids.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the archive.org video URL (shows and ads).</summary>
    public string? Url { get; init; }

    /// <summary>Gets where in the video file this segment starts, ms.</summary>
    public double InMs { get; init; }

    /// <summary>Gets the film title, or tape name for ads.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the release year as text, as the manifest gives it.</summary>
    public string? Year { get; init; }

    /// <summary>Gets the ad volume correction in dB.</summary>
    public double GainDb { get; init; }

    /// <summary>Gets the bump half: "open" (before an ad), "close" (after it) or "full".</summary>
    public string? Half { get; init; }

    /// <summary>Gets the channel's own bump, if any.</summary>
    public BumpPart? Channel { get; init; }

    /// <summary>Gets the network-wide (spelunkr) bump, if any.</summary>
    public BumpPart? Platform { get; init; }

    /// <summary>Gets the bump timing constants.</summary>
    public BumpLayout? Layout { get; init; }

    /// <summary>Gets how far into the segment the lookup time was (lookups at a time only).</summary>
    public long OffsetMs { get; init; }
}

/// <summary>One bump: text slides over a piece of music.</summary>
public sealed record BumpPart(string Id, IReadOnlyList<Slide> Slides, BumpAudio? Audio);

/// <summary>One text slide shown for <see cref="Ms"/>.</summary>
public sealed record Slide(string Text, long Ms);

/// <summary>The bump's music and where in the track it starts.</summary>
public sealed record BumpAudio(string File, string Url, double StartOffsetS);

/// <summary>Bump timing: silent lead-in before the channel slides, tail after each part, gap between parts.</summary>
public sealed record BumpLayout(long LeadInMs, long TailMs, long GapMs);
