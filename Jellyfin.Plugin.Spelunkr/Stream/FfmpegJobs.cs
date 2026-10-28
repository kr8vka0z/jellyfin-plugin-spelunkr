// FfmpegJobs: turns one schedule segment into the ffmpeg command lines that render it as MPEG-TS on stdout.
// Every job outputs the same fixed format, because archive files differ and a player breaks if the stream's
// resolution or codec changes mid-way. The runner tries the jobs in order until one produces output.
using System.Globalization;
using Jellyfin.Plugin.Spelunkr.Schedule;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>A channel's name and color, used for bump and fallback slates.</summary>
/// <param name="Name">Channel name as shown on screen.</param>
/// <param name="Color">Background color, "#RRGGBB".</param>
public sealed record ChannelLook(string Name, string Color);

/// <summary>One ffmpeg run: its arguments and the text files (name to content) it reads from its working folder.</summary>
public sealed record FfmpegJob(IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> TextFiles);

/// <summary>Builds ffmpeg jobs for segments.</summary>
public static class FfmpegJobs
{
    private const string Video = "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p";

    // Pad both tracks past the end of the input, then cut with -t: every job lasts exactly its planned length,
    // even when the file is a little shorter than the manifest says, so the stream never drifts off schedule.
    private const string PadVideo = ",tpad=stop=-1:stop_mode=clone";
    private const string Audio = "aresample=48000,aformat=channel_layouts=stereo";
    private const string Silence = "anullsrc=r=48000:cl=stereo";

    /// <summary>
    /// The jobs for <paramref name="segment"/>, best first: the real thing, then the real thing with silence
    /// (missing or broken audio), then a plain slate naming what should be on, so the stream never stops.
    /// </summary>
    /// <param name="segment">What is on.</param>
    /// <param name="look">The channel's name and color.</param>
    /// <param name="offsetMs">How far into the segment to start.</param>
    /// <param name="durationMs">How long to run.</param>
    /// <param name="outputOffsetS">Where this job sits in the stream's timeline, so timestamps keep counting up.</param>
    public static IReadOnlyList<FfmpegJob> For(Segment segment, ChannelLook look, long offsetMs, long durationMs, double outputOffsetS)
    {
        var tail = Tail(durationMs, outputOffsetS);
        var jobs = new List<FfmpegJob>();
        switch (segment.Kind)
        {
            case "show" or "ad" when segment.Url is not null:
            {
                var input = Net().Concat(["-ss", Seconds(segment.InMs + offsetMs), "-i", segment.Url]).ToList();
                var audio = Audio + (segment.GainDb != 0 ? string.Create(CultureInfo.InvariantCulture, $",volume={segment.GainDb}dB") : string.Empty) + ",apad";
                jobs.Add(new([.. Head, .. input, "-map", "0:v:0", "-map", "0:a:0", "-vf", Video + PadVideo, "-af", audio, .. tail], Empty));
                jobs.Add(new([.. Head, .. input, "-f", "lavfi", "-i", Silence, "-map", "0:v:0", "-map", "1:a", "-vf", Video + PadVideo, .. tail], Empty));
                break;
            }

            case "bump":
            {
                var (filter, files) = Cards(BumpCards.For(segment, look), look, offsetMs);
                // ponytail: one track per bump. A "full" bump has a second track for its network half on the site;
                // here the channel's track plays under both halves.
                var music = segment.Half == "close" ? segment.Platform?.Audio : segment.Channel?.Audio ?? segment.Platform?.Audio;
                if (music is not null)
                {
                    jobs.Add(new(
                        [.. Head, .. Color(look), .. Remote, "-ss", Seconds((music.StartOffsetS * 1000) + offsetMs), "-i", music.Url,
                         "-filter_complex", $"{filter}[v];[1:a]{Audio},apad[a]", "-map", "[v]", "-map", "[a]", .. tail],
                        files));
                }

                jobs.Add(new([.. Head, .. Color(look), "-f", "lavfi", "-i", Silence, "-filter_complex", $"{filter}[v]", "-map", "[v]", "-map", "1:a", .. tail], files));
                break;
            }
        }

        // The last resort: the channel color with the title (or channel name) and silence.
        var slateText = segment.Title ?? look.Name;
        var (slate, slateFiles) = Cards([new BumpCard(offsetMs, offsetMs + durationMs, slateText)], look, offsetMs);
        jobs.Add(new([.. Head, .. Color(look), "-f", "lavfi", "-i", Silence, "-filter_complex", $"{slate}[v]", "-map", "[v]", "-map", "1:a", .. tail], slateFiles));
        return jobs;
    }

    private static readonly Dictionary<string, string> Empty = [];

    // -progress: ffmpeg reports how much it has written (out_time_us) on stderr, so a job that dies part-way can be
    // finished from exactly where it stopped.
    private static string[] Head => ["-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-progress", "pipe:2", "-stats_period", "0.25"];

    // Read inputs at playback speed after a 10 s head start: the head start covers the gap while the next job
    // starts up, and real-time reading stops a 90-minute film being fetched and encoded all at once.
    private static string[] Paced => ["-readrate", "1", "-readrate_initial_burst", "10"];

    private static IEnumerable<string> Net() => [.. Paced, .. Remote];

    // For every input fetched over the network. -rw_timeout (microseconds) makes ffmpeg give up on a server that
    // stops sending, instead of waiting forever; reconnect covers dropped connections.
    private static string[] Remote => ["-rw_timeout", "15000000", "-reconnect", "1", "-reconnect_on_network_error", "1", "-reconnect_delay_max", "5"];

    private static string[] Color(ChannelLook look) =>
        [.. Paced, "-f", "lavfi", "-i", $"color=c=0x{look.Color.TrimStart('#')}:s=1280x720:r=30"];

    // No B-frames (-bf 0): with them each job's first video timestamps start slightly before the previous job's
    // last ones, which players see as time going backwards at every join.
    // ponytail: the audio still overlaps by ~40 ms at each join (AAC encoder priming); players drop the extra
    // packets. A single long-running encoder fed by per-segment decoders would remove that, at more complexity.
    private static string[] Tail(long durationMs, double outputOffsetS) =>
    [
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-maxrate", "3000k", "-bufsize", "6000k",
        "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-bf", "0",
        "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000",
        "-t", Seconds(durationMs), "-output_ts_offset", outputOffsetS.ToString("0.000", CultureInfo.InvariantCulture),
        "-f", "mpegts", "pipe:1",
    ];

    /// <summary>drawtext filters for the cards, timed relative to the job start (the segment start + offset).</summary>
    private static (string Filter, Dictionary<string, string> Files) Cards(IReadOnlyList<BumpCard> cards, ChannelLook look, long offsetMs)
    {
        var files = new Dictionary<string, string>();
        var parts = new List<string> { "[0:v]format=yuv420p" };
        foreach (var card in cards.Where(c => c.EndMs > offsetMs))
        {
            // Text goes through files named in the job's working folder: drawtext's inline quoting rules
            // can't safely carry arbitrary slide text (quotes, colons, percent signs).
            var name = string.Create(CultureInfo.InvariantCulture, $"t{files.Count}.txt");
            files[name] = Wrap(card.Text);
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"drawtext=textfile={name}:expansion=none:fontcolor={TextColor(look.Color)}:fontsize=48:line_spacing=14:text_align=C:x=(w-tw)/2:y=(h-th)/2:enable='gte(t,{(card.StartMs - offsetMs) / 1000.0:0.000})*lt(t,{(card.EndMs - offsetMs) / 1000.0:0.000})'"));
        }

        return (string.Join(",", parts), files);
    }

    /// <summary>Dark text on light channel colors, light text on dark ones.</summary>
    private static string TextColor(string color)
    {
        var rgb = Convert.ToInt32(color.TrimStart('#'), 16);
        var luma = (0.299 * ((rgb >> 16) & 255)) + (0.587 * ((rgb >> 8) & 255)) + (0.114 * (rgb & 255));
        return luma > 140 ? "0x1A1A1A" : "0xF5F5F5";
    }

    /// <summary>Breaks text into lines of about 34 characters so a 48 px line fits a 1280 px frame.</summary>
    private static string Wrap(string text)
    {
        var lines = new List<string>();
        var line = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > 34)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = line.Length == 0 ? word : line + " " + word;
            }
        }

        lines.Add(line);
        return string.Join('\n', lines);
    }

    private static string Seconds(double ms) => (ms / 1000).ToString("0.000", CultureInfo.InvariantCulture);
}
