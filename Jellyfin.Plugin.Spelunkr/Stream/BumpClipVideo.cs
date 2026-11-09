// BumpClipVideo: the ffmpeg filter chain that plays a bump's archive.org clip under its channel slides, timed and
// styled like spelunkr.tv's bump renderer (index-DRgRcbfS.js) and its CSS (index-ZTw_ptYa.css), 2026-11-09 build.
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Spelunkr.Schedule;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>Builds the clip's part of a bump job's filter graph.</summary>
public static class BumpClipVideo
{
    // The site holds the clip this far before its end, so the held frame is a real one (engine constant `cv`).
    private const double HoldBeforeEndS = 0.05;

    // The flash look's white hit on each hold step (CSS .bump-clip-hit): opacity .45 fading to 0 over .22 s, ease-out.
    private const double HitOpacity = 0.45;
    private const double HitS = 0.22;

    private const double Fps = 30;

    /// <summary>
    /// Where in the clip the picture is at <paramref name="atS"/> seconds into the bump: the engine's clip clock (`dv`),
    /// ported because it needs the clip's length, which only ffprobe knows. The clip plays muted so that it ends
    /// exactly when the motion slides do, then holds. A test pins this to the engine's own function.
    /// </summary>
    /// <param name="clip">The clip's plan.</param>
    /// <param name="lengthS">The clip file's length.</param>
    /// <param name="atS">Seconds from the bump's start.</param>
    public static double ClipTimeS(BumpClip clip, double lengthS, double atS)
    {
        if (!(lengthS > 0))
        {
            return 0;
        }

        var (startS, motionEndS) = (clip.StartMs / 1000.0, clip.MotionEndMs / 1000.0);
        var playS = PlayS(clip, lengthS);
        var holdS = HoldS(clip, lengthS);
        if (atS >= motionEndS)
        {
            return holdS;
        }

        if (atS < startS)
        {
            return Math.Max(0, playS - (motionEndS - startS));
        }

        var t = playS - (motionEndS - atS);
        return t <= 0 ? 0 : Math.Min(t, holdS);
    }

    /// <summary>The zoom look's scale on a hold step. The CSS only lists steps 0 to 4; later steps fall back to step 0's.</summary>
    /// <param name="step">The hold step, from 0.</param>
    public static double Zoom(int step) => step switch
    {
        1 => 1.55,
        2 => 1.9,
        3 or 4 => 2.3,
        _ => 1.25,
    };

    /// <summary>
    /// The filter graph's opening for a clip job: the clip (input <paramref name="input"/>) fitted to the card's
    /// <paramref name="width"/>x<paramref name="height"/> on black, timed and styled, laid over the channel color
    /// (input 0) while it is in view. Ends in a yuv420p picture ready for the cards' drawtext filters.
    /// </summary>
    /// <param name="clip">The clip's plan.</param>
    /// <param name="lengthS">The clip file's length.</param>
    /// <param name="offsetMs">How far into the bump the job starts.</param>
    /// <param name="width">Card width.</param>
    /// <param name="height">Card height.</param>
    /// <param name="input">The clip's input number.</param>
    /// <param name="tone">The channel color, "#RRGGBB": the duotone's tint.</param>
    public static string Under(BumpClip clip, double lengthS, long offsetMs, int width, int height, int input, string tone)
    {
        // Seconds from the bump's start (n) until the end, where the job's own clock is t = n - off. The clip chain runs
        // on its own clock u = n - from, starting when it first shows in this job.
        var off = offsetMs / 1000.0;
        var from = Math.Max(clip.StartMs / 1000.0, off);
        var holdS = HoldS(clip, lengthS);
        var playFromS = (clip.MotionEndMs / 1000.0) - PlayS(clip, lengthS);
        // A clip shorter than its motion slides waits on its first frame; otherwise it joins at the engine's moment.
        var waitS = Math.Max(0, playFromS - from);
        var trimFrom = Math.Clamp(waitS > 0 ? 0 : ClipTimeS(clip, lengthS, from), 0, Math.Max(0, holdS - (1 / Fps)));
        var trimTo = Math.Max(holdS, trimFrom + (1 / Fps));

        // Hold steps on the clip chain's clock, with their step numbers.
        var holds = clip.Slides.Where(s => s.HoldStep >= 0)
            .Select(s => (Step: s.HoldStep, From: (s.StartMs / 1000.0) - from, To: (s.EndMs / 1000.0) - from))
            .Where(h => h.To > 0).ToList();

        var c = new StringBuilder();
        c.Append(F($"[{input}:v]setpts=PTS-STARTPTS,fps=30,scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,format=rgb24"));
        c.Append(F($",trim=start={trimFrom:0.000}:end={trimTo:0.000},setpts=PTS-STARTPTS,tpad=start_duration={waitS:0.000}:start_mode=clone:stop=-1:stop_mode=clone"));
        if (holds.Count > 0 && clip.Look is "zoom" or "duozoom")
        {
            // A step's zoom keeps the zoomAt point still (CSS transform-origin): the visible part starts at o(1 - 1/z).
            var (ox, oy) = ZoomAt(clip.ZoomAt);
            var z = holds.Aggregate("1", (rest, h) => F($"if({Between("it", h.From, h.To)},{Zoom(h.Step):0.00},{rest})"));
            c.Append(F($",zoompan=z='{z}':x='iw*{ox:0.000}*(1-1/zoom)':y='ih*{oy:0.000}*(1-1/zoom)':d=1:s={width}x{height}:fps=30,format=rgb24"));
        }

        if (holds.Count > 0 && clip.Look is "duotone" or "duozoom")
        {
            // CSS grayscale(1) contrast(1.35) brightness(1.05), invert(1) on odd steps, then the .bump-clip-tone layer:
            // the channel color mixed 62% with black, multiplied over the clip.
            var all = Any(holds);
            var odd = Any(holds.Where(h => h.Step % 2 == 1));
            var rgb = Convert.ToInt32(tone.TrimStart('#'), 16);
            double Tint(int shift) => 0.62 * ((rgb >> shift) & 255) / 255;
            c.Append(F($",colorchannelmixer=rr=0.2126:rg=0.7152:rb=0.0722:gr=0.2126:gg=0.7152:gb=0.0722:br=0.2126:bg=0.7152:bb=0.0722:enable='{all}'"));
            const string Contrast = "'clip(clip((val-127.5)*1.35+127.5,0,255)*1.05,0,255)'";
            c.Append(F($",lutrgb=r={Contrast}:g={Contrast}:b={Contrast}:enable='{all}'"));
            if (odd != "0")
            {
                c.Append(F($",negate=enable='{odd}'"));
            }

            c.Append(F($",lutrgb=r=val*{Tint(16):0.0000}:g=val*{Tint(8):0.0000}:b=val*{Tint(0):0.0000}:enable='{all}'"));
        }

        c.Append(",format=yuv420p");
        if (clip.Look == "flash")
        {
            // One box per frame of the fade: drawbox's color can't change over time.
            foreach (var h in holds)
            {
                for (var f = 0; f < HitS * Fps; f++)
                {
                    var alpha = HitOpacity * (1 - EaseOut(f / Fps / HitS));
                    var at = h.From + (f / Fps);
                    c.Append(F($",drawbox=x=0:y=0:w=iw:h=ih:color=white@{alpha:0.000}:t=fill:enable='{Between("t", at, at + (1 / Fps))}'"));
                }
            }
        }

        var shown = (From: from - off, To: (clip.EndMs / 1000.0) - off);
        c.Append(F($",setpts=PTS+{shown.From:0.000}/TB[clip];[0:v]format=yuv420p[bg];[bg][clip]overlay=eof_action=pass:enable='{Between("t", shown.From, shown.To)}',format=yuv420p"));
        return c.ToString();
    }

    private static double PlayS(BumpClip clip, double lengthS) => clip.To is > 0 && clip.To < lengthS ? clip.To.Value : lengthS;

    private static double HoldS(BumpClip clip, double lengthS)
    {
        var playS = PlayS(clip, lengthS);
        return playS < lengthS ? playS : Math.Max(0, lengthS - HoldBeforeEndS);
    }

    /// <summary>"X% Y%" as fractions; the middle when missing (the engine already rejects anything else).</summary>
    private static (double X, double Y) ZoomAt(string? at)
    {
        var parts = (at ?? string.Empty).Split(' ');
        return parts.Length == 2
            && double.TryParse(parts[0].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(parts[1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            ? (Math.Clamp(x, 0, 100) / 100, Math.Clamp(y, 0, 100) / 100)
            : (0.5, 0.5);
    }

    // CSS ease-out, approximated: over a 7-frame fade a quadratic is indistinguishable from cubic-bezier(0, 0, .58, 1).
    private static double EaseOut(double p) => 1 - ((1 - p) * (1 - p));

    private static string Any(IEnumerable<(int Step, double From, double To)> windows)
    {
        var parts = windows.Select(w => Between("t", w.From, w.To)).ToList();
        return parts.Count == 0 ? "0" : string.Join("+", parts);
    }

    private static string Between(string v, double from, double to) => F($"gte({v},{from:0.000})*lt({v},{to:0.000})");

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
