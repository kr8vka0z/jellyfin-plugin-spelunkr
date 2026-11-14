// NowPlaying: the words in the TV cabinet's now-playing window: which film, under which label, broken into lines
// that fit. FfmpegJobs draws them; the window itself is part of the cabinet art (tools/cabinet/render.ts).
using Jellyfin.Plugin.Spelunkr.Schedule;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>What the now-playing window shows.</summary>
/// <param name="Label">"NOW PLAYING" during a film, "UP NEXT" between films.</param>
/// <param name="Title">The film's title.</param>
/// <param name="Year">The film's year; null draws no year line.</param>
public sealed record NowPlaying(string Label, string Title, string? Year)
{
    /// <summary>Most title lines the window holds; a longer title ends in "…" on the last.</summary>
    public const int MaxLines = 4;

    /// <summary>A title line's widest at 1080p: the window's 174 px less its 4 px rim and 6 px each side.</summary>
    public const double LinePx = 174 - (2 * 4) - (2 * 6);

    /// <summary>The title's font size at 1080p, as in the approved mockup.</summary>
    public const double TitlePx = 23;

    // The smallest the title shrinks to so a long single word (Frankenstein) fits whole; past that it is broken.
    private const double MinTitlePx = 17;

    // How far the schedule is searched for the next film; the bumps and ads between two films are a few segments.
    private const int MaxHops = 50;

    // Glyph widths (ems) of DejaVu Serif Bold, the face fontconfig gives "serif:bold" in Jellyfin's Docker image.
    // ponytail: measured for that one font, rounded up; another serif is close but may run a little wider, which
    // the 6 px side margin absorbs. Anything not listed counts as an em, the widest a letter gets.
    private static readonly (double Ems, string Chars)[] Widths =
    [
        (0.35, " ,.'"), (0.38, "j|/:;\\il"), (0.47, "-f!tIJ()[]"), (0.53, "_`\"*r"), (0.59, "szvy?"), (0.61, "xc"),
        (0.65, "e{}a"), (0.7, "ok0123456789bdgpqL$"), (0.74, "FYSZThnu"), (0.78, "PEAVX"), (0.85, "CRBG#+<=>^~"),
        (0.87, "wDKOQU"), (0.95, "&NH%"), (1.12, "mMW"),
    ];

    /// <summary>The window for <paramref name="segment"/>: a film names itself; anything between films names the next one.</summary>
    /// <param name="segment">What is on.</param>
    /// <param name="nextShow">The next film after it (see <see cref="NextShowAsync"/>); only needed between films.</param>
    /// <returns>Null when there is no film to name.</returns>
    public static NowPlaying? For(Segment segment, Segment? nextShow) =>
        segment.Kind == "show" ? Of("NOW PLAYING", segment) : nextShow is null ? null : Of("UP NEXT", nextShow);

    /// <summary>The first film after <paramref name="segment"/>, or null if none is found within a few segments.</summary>
    public static async Task<Segment?> NextShowAsync(Segment segment, Func<long, CancellationToken, Task<Segment>> segmentAt, CancellationToken ct)
    {
        var atMs = segment.EndMs;
        for (var i = 0; i < MaxHops; i++)
        {
            var next = await segmentAt(atMs, ct).ConfigureAwait(false);
            if (next.Kind == "show")
            {
                return next;
            }

            if (next.EndMs <= atMs)
            {
                return null;
            }

            atMs = next.EndMs;
        }

        return null;
    }

    /// <summary>Gets the title's font size at 1080p: <see cref="TitlePx"/>, or smaller when its longest word wouldn't fit a line.</summary>
    public double TitleSize => Math.Clamp(LinePx / Title.Split(' ').Max(Ems), MinTitlePx, TitlePx);

    // A line's widest in ems of the title at its size.
    private double LineEms => LinePx / TitleSize;

    /// <summary>The title broken into lines that fit the window at <see cref="TitleSize"/>, at most <see cref="MaxLines"/>.</summary>
    public IReadOnlyList<string> TitleLines()
    {
        var lines = FfmpegJobs.WrapLine(Title, LineEms, Ems).Split('\n').SelectMany(Broken).ToList();
        if (lines.Count <= MaxLines)
        {
            return lines;
        }

        // Fill the last line with as many whole words of the rest as fit before the "…" ("Love of Martha…", not
        // "Love of Martha Iv…"). A first word too wide on its own is cut by letters.
        var last = string.Join(' ', lines.Skip(MaxLines - 1));
        while (Ems(last + "…") > LineEms && last.LastIndexOf(' ') is var space and > 0)
        {
            last = last[..space];
        }

        while (last.Length > 0 && Ems(last.TrimEnd() + "…") > LineEms)
        {
            last = last[..^1];
        }

        return [.. lines.Take(MaxLines - 1), last.TrimEnd() + "…"];
    }

    /// <summary>The width of <paramref name="text"/> in ems of the title font.</summary>
    internal static double Ems(string text) => text.Sum(c => Widths.FirstOrDefault(w => w.Chars.Contains(c, StringComparison.Ordinal)).Ems is var e and > 0 ? e : 1);

    private static NowPlaying? Of(string label, Segment film) =>
        string.IsNullOrWhiteSpace(film.Title) ? null : new(label, film.Title.Trim(), string.IsNullOrWhiteSpace(film.Year) ? null : film.Year.Trim());

    // A single word too wide for a line, cut into pieces that fit (wrapping only breaks at spaces).
    private IEnumerable<string> Broken(string line)
    {
        while (Ems(line) > LineEms)
        {
            var n = 1;
            while (Ems(line[..(n + 1)]) <= LineEms)
            {
                n++;
            }

            yield return line[..n];
            line = line[n..];
        }

        yield return line;
    }
}
