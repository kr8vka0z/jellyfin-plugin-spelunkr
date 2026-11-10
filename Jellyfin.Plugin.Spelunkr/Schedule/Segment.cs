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

    /// <summary>Gets "show", "ad", "bump" or "up-next" (anything else is a kind this plugin shows as a slate).</summary>
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

    /// <summary>Gets the clip that plays under the channel half (open and full bumps whose slides name one).</summary>
    public BumpClip? Clip { get; init; }

    /// <summary>Gets the bump timing constants.</summary>
    public BumpLayout? Layout { get; init; }

    /// <summary>Gets the film's position in its manifest (and details file); shows only.</summary>
    public int? VideoIndex { get; init; }

    /// <summary>Gets the title of the film an "up-next" card announces.</summary>
    public string? Next { get; init; }

    /// <summary>Gets the music under an "up-next" card, if any.</summary>
    public BumpAudio? Sting { get; init; }

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

/// <summary>
/// An archive.org clip under a bump's channel slides, as the engine's clip helpers plan it. Times are ms from the bump's
/// start, laid out the way the site's bump renderer lays out the channel slides (see schedule.js).
/// </summary>
/// <param name="Src">The clip's archive.org download URL.</param>
/// <param name="To">Where the clip is cut off, seconds; null for its whole length.</param>
/// <param name="Look">"flash", "duotone", "zoom" or "duozoom".</param>
/// <param name="WordsOver">Whether the words show while the clip moves.</param>
/// <param name="ZoomAt">The zoom's fixed point, "X% Y%"; null for the middle.</param>
/// <param name="First">The slide the clip starts on.</param>
/// <param name="Motion">How many slides, from <paramref name="First"/>, the clip plays over.</param>
/// <param name="StartMs">When the clip comes into view.</param>
/// <param name="MotionEndMs">When the clip ends and holds its last frame.</param>
/// <param name="EndMs">When the clip leaves (the end of the last channel slide).</param>
/// <param name="Slides">Each channel slide's timing, one per slide.</param>
public sealed record BumpClip(string Src, double? To, string Look, bool WordsOver, string? ZoomAt, int First, int Motion, long StartMs, long MotionEndMs, long EndMs, IReadOnlyList<ClipSlide> Slides);

/// <summary>One channel slide of a clip bump.</summary>
/// <param name="StartMs">When it shows, ms from the bump's start.</param>
/// <param name="EndMs">When it ends.</param>
/// <param name="HoldStep">Which hold step it is after the clip ends (0, 1, ...), or -1 before that.</param>
/// <param name="Hidden">Whether its words are hidden (while the clip moves, unless the clip allows words over it).</param>
public sealed record ClipSlide(long StartMs, long EndMs, int HoldStep, bool Hidden);
