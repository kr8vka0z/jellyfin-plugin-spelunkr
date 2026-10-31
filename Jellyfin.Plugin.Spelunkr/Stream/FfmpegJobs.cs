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

/// <summary>
/// A stream's one video format: 1280x720 encoded on the CPU, or 1920x1080 upscaled and encoded on a VA-API GPU.
/// </summary>
/// <param name="Width">Frame width.</param>
/// <param name="Height">Frame height.</param>
/// <param name="MaxRateK">Video bitrate cap, kbit/s.</param>
/// <param name="VaapiDevice">The GPU's device path from Jellyfin's settings; null for a CPU stream.</param>
/// <param name="Cabinet">The channel's TV cabinet image at this size (see <see cref="CabinetArt"/>); null for a plain full-frame picture.</param>
public sealed record Picture(int Width, int Height, int MaxRateK, string? VaapiDevice, string? Cabinet = null)
{
    /// <summary>Gets the left edge of the cabinet's tube, in pixels. Must match the art (tools/cabinet/render.ts).</summary>
    public int TubeX => Width * 312 / 1920;

    /// <summary>Gets the top edge of the tube.</summary>
    public int TubeY => Height * 54 / 1080;

    /// <summary>Gets the tube's width: exactly 4:3, so an old square film fills it.</summary>
    public int TubeWidth => Width * 1296 / 1920;

    /// <summary>Gets the tube's height.</summary>
    public int TubeHeight => Height * 972 / 1080;

    // 5000k at 720p: the cap only bites on busy, grainy scenes, which is where old film looked blocky at the old
    // 3000k, while staying an easy stream for any home network or player. 8000k is the same per-pixel budget at 1080p.

    /// <summary>Gets the CPU format.</summary>
    public static Picture Cpu { get; } = new(1280, 720, 5000, null);

    /// <summary>The GPU format, on the VA-API device Jellyfin is set to use.</summary>
    /// <param name="vaapiDevice">Device path from Jellyfin's encoding settings.</param>
    public static Picture Gpu(string vaapiDevice) => new(1920, 1080, 8000, vaapiDevice);
}

/// <summary>Builds ffmpeg jobs for segments.</summary>
public static class FfmpegJobs
{
    // Pad both tracks past the end of the input, then cut with -t: every job lasts exactly its planned length,
    // even when the file is a little shorter than the manifest says, so the stream never drifts off schedule.
    private const string PadVideo = ",tpad=stop=-1:stop_mode=clone";
    private const string Audio = "aresample=48000,aformat=channel_layouts=stereo";
    private const string Silence = "anullsrc=r=48000:cl=stereo";

    /// <summary>
    /// The jobs for <paramref name="segment"/>, best first: the real thing, then the real thing with silence
    /// (missing or broken audio), then a plain slate naming what should be on, so the stream never stops.
    /// A GPU stream runs that whole list on the GPU, then all of it again on the CPU at the same size, so a GPU
    /// that fails only costs the picture's sharpness, never the channel.
    /// </summary>
    /// <param name="segment">What is on.</param>
    /// <param name="look">The channel's name and color.</param>
    /// <param name="offsetMs">How far into the segment to start.</param>
    /// <param name="durationMs">How long to run.</param>
    /// <param name="outputOffsetS">Where this job sits in the stream's timeline, so timestamps keep counting up.</param>
    /// <param name="originalUrl">
    /// A better copy of <see cref="Segment.Url"/> (same film, same length; see <see cref="ArchiveOriginals"/>),
    /// tried first with Spelunkr's own file behind it. Must stay the same for every call about one segment: the
    /// player picks fallbacks by position in this list.
    /// </param>
    /// <param name="picture">The stream's format; CPU 720p when not given.</param>
    public static IReadOnlyList<FfmpegJob> For(Segment segment, ChannelLook look, long offsetMs, long durationMs, double outputOffsetS, string? originalUrl = null, Picture? picture = null)
    {
        var p = picture ?? Picture.Cpu;
        // Each entry builds one job, for the GPU (true) or the CPU (false).
        var jobs = new List<Func<bool, FfmpegJob>>();
        string[] Tail(bool gpu) => Encode(p, gpu, durationMs, outputOffsetS);
        switch (segment.Kind)
        {
            case "show" or "ad" when segment.Url is not null:
            {
                var audio = Audio + (segment.GainDb != 0 ? string.Create(CultureInfo.InvariantCulture, $",volume={segment.GainDb}dB") : string.Empty) + ",apad";
                List<string> Input(string url) => [.. Net(), "-ss", Seconds(segment.InMs + offsetMs), "-i", url];
                if (originalUrl is not null)
                {
                    jobs.Add(gpu => new([.. Head, .. FilmDevices(p, gpu), .. Input(originalUrl), .. FilmVideo(p, gpu, 1), "-map", "0:a:0", "-af", audio, .. Tail(gpu)], Empty));
                }

                var input = Input(segment.Url);
                jobs.Add(gpu => new([.. Head, .. FilmDevices(p, gpu), .. input, .. FilmVideo(p, gpu, 1), "-map", "0:a:0", "-af", audio, .. Tail(gpu)], Empty));
                jobs.Add(gpu => new([.. Head, .. FilmDevices(p, gpu), .. input, "-f", "lavfi", "-i", Silence, .. FilmVideo(p, gpu, 2), "-map", "1:a", .. Tail(gpu)], Empty));
                break;
            }

            case "bump" or "up-next":
            {
                // An "up next" card names the coming film over a slice of music (the sting), like a one-slide bump.
                // ponytail: the site animates the card's words onto the sting's beats; here it's one still card.
                var cards = segment.Kind == "bump"
                    ? BumpCards.For(segment, look)
                    : [new BumpCard(0, segment.LengthMs, $"UP NEXT ON {look.Name}" + (segment.Next is null ? string.Empty : $"\n\n{segment.Next}"))];
                var (filter, files) = Cards(cards, look, offsetMs, p);
                // ponytail: one track per bump. A "full" bump has a second track for its network half on the site;
                // here the channel's track plays under both halves.
                var music = segment.Kind == "up-next" ? segment.Sting
                    : segment.Half == "close" ? segment.Platform?.Audio : segment.Channel?.Audio ?? segment.Platform?.Audio;
                if (music is not null)
                {
                    jobs.Add(gpu => new(
                        [.. Head, .. CardDevices(p, gpu), .. Color(look, p), .. Remote, "-ss", Seconds((music.StartOffsetS * 1000) + offsetMs), "-i", music.Url,
                         .. CardVideo(p, gpu, filter, $";[1:a]{Audio},apad[a]"), "-map", "[v]", "-map", "[a]", .. Tail(gpu)],
                        files));
                }

                jobs.Add(gpu => new([.. Head, .. CardDevices(p, gpu), .. Color(look, p), "-f", "lavfi", "-i", Silence, .. CardVideo(p, gpu, filter), "-map", "[v]", "-map", "1:a", .. Tail(gpu)], files));
                break;
            }
        }

        // The last resort: the channel color with the title (or channel name) and silence.
        var slateText = segment.Title ?? look.Name;
        var (slate, slateFiles) = Cards([new BumpCard(offsetMs, offsetMs + durationMs, slateText)], look, offsetMs, p);
        jobs.Add(gpu => new([.. Head, .. CardDevices(p, gpu), .. Color(look, p), "-f", "lavfi", "-i", Silence, .. CardVideo(p, gpu, slate), "-map", "[v]", "-map", "1:a", .. Tail(gpu)], slateFiles));

        // On the GPU each job is followed by its CPU twin, so a failed GPU film is retried as the same film on the CPU
        // before anything further down the list (a GPU title slate would otherwise fill the rest of the film).
        // ponytail: a GPU that is broken outright fails each segment's first GPU job before its CPU twin runs.
        // That failure is immediate (the device won't open), so it costs a moment per segment, not a stall.
        return p.VaapiDevice is null
            ? jobs.Select(j => j(false)).ToList()
            : jobs.SelectMany(j => new[] { j(true), j(false) }).ToList();
    }

    /// <summary>
    /// The card that opens a stream: the channel's color, its name and the film that's on, with silence. It starts
    /// the stream's timeline, so its timestamps begin at 0. The GPU job is followed by its CPU twin.
    /// </summary>
    /// <param name="segment">What is on.</param>
    /// <param name="look">The channel's name and color.</param>
    /// <param name="durationMs">How long to run.</param>
    /// <param name="picture">The stream's format; CPU 720p when not given.</param>
    public static IReadOnlyList<FfmpegJob> TuningIn(Segment segment, ChannelLook look, long durationMs, Picture? picture = null)
    {
        var p = picture ?? Picture.Cpu;
        var text = "TUNING IN\n\n" + look.Name + (segment.Kind == "show" && segment.Title is not null ? "\n\n" + segment.Title : string.Empty);
        var (filter, files) = Cards([new BumpCard(0, durationMs, text)], look, 0, p);
        FfmpegJob Job(bool gpu) => new([.. Head, .. CardDevices(p, gpu), .. Color(look, p), "-f", "lavfi", "-i", Silence, .. CardVideo(p, gpu, filter), "-map", "[v]", "-map", "1:a", .. Encode(p, gpu, durationMs, 0)], files);
        return p.VaapiDevice is null ? [Job(false)] : [Job(true), Job(false)];
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

    private static string[] Color(ChannelLook look, Picture p) =>
        [.. Paced, "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"color=c=0x{look.Color.TrimStart('#')}:s={(p.Cabinet is null ? p.Width : p.TubeWidth)}x{(p.Cabinet is null ? p.Height : p.TubeHeight)}:r=30")];

    // Films on the GPU: libplacebo (a Vulkan filter) upscales, so Vulkan is opened on top of the VA-API device and
    // the frames are handed back to VA-API for the encoder without leaving the GPU.
    private static string[] FilmDevices(Picture p, bool gpu) => gpu
        ? ["-init_hw_device", "vaapi=va:" + p.VaapiDevice, "-init_hw_device", "vulkan=vk@va", "-filter_hw_device", "vk"]
        : [];

    // Cards are drawn on the CPU (drawtext) and only uploaded for the GPU encoder. With a cabinet they go through the
    // same GPU filter as films, so the wood stays the same shade across a film-to-card join.
    private static string[] CardDevices(Picture p, bool gpu) => p.Cabinet is not null ? FilmDevices(p, gpu)
        : gpu ? ["-init_hw_device", "vaapi=va:" + p.VaapiDevice, "-filter_hw_device", "va"] : [];

    private static string Upload(bool gpu) => gpu ? ",format=nv12,hwupload" : string.Empty;

    // The cabinet goes last among a job's inputs, so the film's, the music's and the silence's numbers never change.
    private static string[] CabinetInput(Picture p) => p.Cabinet is null ? [] : ["-framerate", "30", "-i", p.Cabinet];

    private static string[] FilmVideo(Picture p, bool gpu, int cabinetInput) => p.Cabinet is null
        ? ["-map", "0:v:0", "-vf", Film(p, gpu)]
        : [.. CabinetInput(p), "-filter_complex", Framed(p, gpu, "[0:v]" + (gpu ? Denoised : TubeFit(p)), cabinetInput), "-map", "[v]"];

    private static string[] CardVideo(Picture p, bool gpu, string card, string more = "") =>
        [.. CabinetInput(p), "-filter_complex", (p.Cabinet is null ? card + Upload(gpu) + "[v]" : Framed(p, gpu, card, 2)) + more];

    /// <summary>
    /// Draws the cabinet over <paramref name="video"/>, which is a filter chain ending in a yuv420p picture, and
    /// labels the result [v]. The cabinet is decoded and handed to the loop once: redoing that for every frame took
    /// a GPU film from 10x playback speed to 3x, for a picture that never changes.
    /// </summary>
    /// <remarks>
    /// Both routes convert the picture to bt709 and pin the cabinet's color handling. Left alone it follows each
    /// film's own color tags, and the wood visibly changes shade at a join. The film must be converted, not relabeled,
    /// or a film in another color space plays with the wrong colors.
    /// </remarks>
    private static string Framed(Picture p, bool gpu, string video, int cabinetInput) => gpu
        ? string.Create(CultureInfo.InvariantCulture, $"{video},hwupload[a];[{cabinetInput}:v]format=rgba,setparams=color_trc=bt709:color_primaries=bt709,hwupload,loop=-1:1:0[b];[a][b]libplacebo=inputs=2:w={p.Width}:h={p.Height}:fps=30:fit_mode=contain:fillcolor=black:upscaler=ewa_lanczossharp:deband=true:format=nv12:colorspace=bt709:range=tv:color_primaries=bt709:color_trc=bt709:{Place("x", p.TubeX, 0)}:{Place("y", p.TubeY, 0)}:{Place("w", p.TubeWidth, p.Width)}:{Place("h", p.TubeHeight, p.Height)},hwmap=derive_device=vaapi,scale_vaapi=format=nv12,setsar=1[v]")
        : string.Create(CultureInfo.InvariantCulture, $"{video},pad={p.Width}:{p.Height}:(ow-iw)/2:(oh-ih)/2,scale=out_color_matrix=bt709:out_range=tv[a];[{cabinetInput}:v]format=yuva420p,loop=-1:1:0[b];[a][b]overlay,format=yuv420p,setsar=1[v]");

    // libplacebo's placement for its two inputs: the film (input 0) in the tube, the cabinet (input 1) over the frame.
    private static string Place(string axis, int film, int cabinet) =>
        string.Create(CultureInfo.InvariantCulture, $"pos_{axis}=if(eq(in_idx\\,0)\\,{film}\\,{cabinet})");

    // A film on the CPU, sized to the tube: scaled down to fit, never stretched, 30 fps. Framed centers it.
    private static string TubeFit(Picture p) =>
        string.Create(CultureInfo.InvariantCulture, $"scale={p.TubeWidth}:{p.TubeHeight}:force_original_aspect_ratio=decrease,setsar=1,fps=30,format=yuv420p{PadVideo}");

    /// <summary>The film's video filter: fit inside the frame with black side bars (never stretched), 30 fps.</summary>
    private static string Film(Picture p, bool gpu) => gpu
        // Then libplacebo's ewa_lanczossharp upscale with debanding (smooths the stepped gradients of the old ~1 Mbps
        // encodes). Measured ~8x real time on a home GPU.
        ? string.Create(CultureInfo.InvariantCulture, $"{Denoised},hwupload,libplacebo=w={p.Width}:h={p.Height}:fit_mode=contain:fillcolor=black:upscaler=ewa_lanczossharp:deband=true:format=nv12,hwmap=derive_device=vaapi,scale_vaapi=format=nv12,setsar=1")
        : string.Create(CultureInfo.InvariantCulture, $"scale={p.Width}:{p.Height}:force_original_aspect_ratio=decrease,pad={p.Width}:{p.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p{PadVideo}");

    // A light denoise (hqdn3d, half its default strength) on the small source frame, so the sharp scaler that follows
    // doesn't sharpen film grain and compression blocks too.
    private static string Denoised => $"hqdn3d=2:1.5:3:2.25,fps=30{PadVideo},format=yuv420p";

    // No B-frames (-bf 0): with them each job's first video timestamps start slightly before the previous job's
    // last ones, which players see as time going backwards at every join.
    // ponytail: the audio still overlaps by ~40 ms at each join (AAC encoder priming); players drop the extra
    // packets. A single long-running encoder fed by per-segment decoders would remove that, at more complexity.
    // Profile, level and a keyframe every 60 frames are pinned on both encoders: a GPU stream mixes GPU jobs with
    // CPU fallbacks, and players expect those to stay the same mid-stream. (h264_vaapi makes every keyframe a fresh
    // start point, as libx264 does with these settings.)
    private static string[] Encode(Picture p, bool gpu, long durationMs, double outputOffsetS) =>
    [
        .. gpu
            ? (string[])["-c:v", "h264_vaapi", "-profile:v", "high", "-level:v", "4.1", "-rc_mode", "VBR", "-b:v", "6000k"]
            : ["-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-level:v", "4.1", "-crf", "23"],
        "-maxrate", Kbps(p.MaxRateK), "-bufsize", Kbps(p.MaxRateK * 2),
        .. gpu ? (string[])["-g", "60", "-bf", "0"] : ["-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-bf", "0"],
        "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000",
        "-t", Seconds(durationMs), "-output_ts_offset", outputOffsetS.ToString("0.000", CultureInfo.InvariantCulture),
        "-f", "mpegts", "pipe:1",
    ];

    private static string Kbps(int k) => k.ToString(CultureInfo.InvariantCulture) + "k";

    /// <summary>drawtext filters for the cards, timed relative to the job start (the segment start + offset).</summary>
    private static (string Filter, Dictionary<string, string> Files) Cards(IReadOnlyList<BumpCard> cards, ChannelLook look, long offsetMs, Picture p)
    {
        // Sizes are set for a 720-line frame and scaled with it, so a card looks the same at 1080p (and wraps the same).
        // In the narrower tube the words shrink less than the width does, so they wrap sooner.
        var (font, spacing, wrap) = p.Cabinet is null ? (48, 14, 34) : (40, 12, 30);
        var fontSize = font * p.Height / 720;
        var lineSpacing = spacing * p.Height / 720;
        var files = new Dictionary<string, string>();
        var parts = new List<string> { "[0:v]format=yuv420p" };
        foreach (var card in cards.Where(c => c.EndMs > offsetMs))
        {
            // Text goes through files named in the job's working folder: drawtext's inline quoting rules
            // can't safely carry arbitrary slide text (quotes, colons, percent signs).
            var name = string.Create(CultureInfo.InvariantCulture, $"t{files.Count}.txt");
            files[name] = Wrap(card.Text, wrap);
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"drawtext=textfile={name}:expansion=none:fontcolor={TextColor(look.Color)}:fontsize={fontSize}:line_spacing={lineSpacing}:text_align=C:x=(w-tw)/2:y=(h-th)/2:enable='gte(t,{(card.StartMs - offsetMs) / 1000.0:0.000})*lt(t,{(card.EndMs - offsetMs) / 1000.0:0.000})'"));
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

    /// <summary>Breaks text into lines of about <paramref name="width"/> characters so a line fits (the font scales with the picture).</summary>
    private static string Wrap(string text, int width) => string.Join('\n', text.Split('\n').Select(t => WrapLine(t, width)));

    private static string WrapLine(string text, int width)
    {
        var lines = new List<string>();
        var line = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
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
