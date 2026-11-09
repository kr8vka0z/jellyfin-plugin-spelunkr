// BumpClipTests: a bump's archive.org clip under its channel slides. Pins what would quietly drift from spelunkr.tv
// (when the clip ends and holds, which look draws what) and that a missing or failing clip leaves today's break as is.
using System.Globalization;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Jellyfin.Plugin.Spelunkr.Stream;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class BumpClipTests
{
    private const string Src = "https://archive.org/download/1953_Movie_Trailers_Intermission_Six_Minutes/1953_Movie_Trailers_Intermission_Six_Minutes_00_13_05_12_3mb.mp4";
    private static readonly ChannelLook Gumshoe = new("GUMSHOE", "#E8DFC8");
    private static readonly ReadyClip Clip = new("/clips/c.mp4", 12.545879);

    /// <summary>
    /// gs-075 (StreamPlanTests.OpenBump) with a made-up clip on its first slide, the second slide under it, and the last
    /// two as hold steps: the same manifest edit a preview build makes.
    /// </summary>
    internal static Segment ClipBump(string look = "duozoom", string? zoomAt = null)
    {
        var engine = new ScheduleEngine();
        var lineup = JsonNode.Parse(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "lineup.json")))!;
        var channel = lineup["channels"]!.AsArray().First(c => (string?)c!["id"] == "gumshoe")!;
        foreach (var pointer in channel["versions"]!.AsArray())
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, (string)pointer!["manifest"]!)))!;
            foreach (var bump in manifest["bumps"]!.AsArray().Where(b => (string?)b!["id"] == "gs-075"))
            {
                var slides = bump!["slides"]!.AsArray();
                slides[0]!["clip"] = Src;
                slides[0]!["track"] = "slowerpace-settling-the-score.mp3";
                slides[0]!["look"] = look;
                if (zoomAt is not null)
                {
                    slides[0]!["zoomAt"] = zoomAt;
                }

                slides[1]!["under"] = true;
            }

            var platform = File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, (string)manifest["platform"]!));
            engine.AddVersion("gumshoe", manifest.ToJsonString(), platform, pointer.ToJsonString());
        }

        return engine.SegmentsBetween("gumshoe", 1_793_220_818_819, 1_793_220_818_820, "https://audio.spelunkr.tv/bumps/")[0];
    }

    private static string Graph(FfmpegJob job) => job.Args[job.Args.ToList().IndexOf("-filter_complex") + 1];

    private static string Ms(long ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    [Fact]
    public void The_engine_plans_the_clip_over_the_site_layout_of_the_channel_slides()
    {
        var clip = ClipBump().Clip!;

        // Lead-in 3.5 s; the first slide gets the site's extra second (2875 + 1000 ms), then 5185, 3975, 2500 ms.
        Assert.Equal((0, 2, 3_500L, 12_560L, 19_035L), (clip.First, clip.Motion, clip.StartMs, clip.MotionEndMs, clip.EndMs));
        Assert.Equal([-1, -1, 0, 1], clip.Slides.Select(s => s.HoldStep));
        Assert.Equal([true, true, false, false], clip.Slides.Select(s => s.Hidden));
        Assert.Null(StreamPlanTests.OpenBump().Clip);
    }

    [Theory]
    [InlineData(12.545879, null)] // longer than the motion slides: joins part-way, ends with them
    [InlineData(4.0, null)] // shorter: waits on its first frame, then ends with them
    [InlineData(12.545879, 6.0)] // cut off at clipTo
    public void Clip_clock_matches_the_engine(double lengthS, double? to)
    {
        var clip = ClipBump().Clip! with { To = to };
        var js = new Engine();
        using (var engineJs = typeof(ScheduleEngine).Assembly.GetManifestResourceStream("engine.js")!)
        using (var reader = new StreamReader(engineJs))
        {
            js.Modules.Add("./engine.js", reader.ReadToEnd());
        }

        js.Modules.Add("t", "import { clipAt } from './engine.js'; export const at = (s, m, e, len, n, to) => clipAt({ startS: s, motionEndS: m, endS: e }, len, n, to).t;");
        var at = js.Modules.Import("t").Get("at");

        for (var n = 0.0; n < 20.5; n += 0.1)
        {
            var engine = js.Invoke(at, clip.StartMs / 1000.0, clip.MotionEndMs / 1000.0, clip.EndMs / 1000.0, lengthS, n, to is null ? Jint.Native.JsValue.Null : to.Value).AsNumber();
            Assert.Equal(engine, BumpClipVideo.ClipTimeS(clip, lengthS, n), 6);
        }
    }

    [Theory]
    [InlineData(0, "trim=start=3.486:end=12.496", "tpad=start_duration=0.000", "setpts=PTS+3.500/TB")] // plays 3.486-12.496 s of the clip over 3.5-12.56 s
    [InlineData(8_000, "trim=start=7.986:end=12.496", "tpad=start_duration=0.000", "setpts=PTS+0.000/TB")] // joined mid-motion: same moment of the clip
    [InlineData(14_000, "trim=start=12.463:end=12.496", "tpad=start_duration=0.000", "setpts=PTS+0.000/TB")] // joined in a hold step: the held frame
    public void Clip_ends_with_the_motion_slides_then_holds(long offsetMs, string trim, string wait, string shift)
    {
        var bump = ClipBump();

        var graph = Graph(FfmpegJobs.For(bump, Gumshoe, offsetMs, bump.LengthMs - offsetMs, 0, clip: Clip)[0]);

        Assert.Contains(trim, graph, StringComparison.Ordinal);
        Assert.Contains(wait, graph, StringComparison.Ordinal);
        Assert.Contains(shift, graph, StringComparison.Ordinal);
        // In view from the first clip slide to the end of the last channel slide, on this job's clock.
        Assert.Contains($"overlay=eof_action=pass:enable='gte(t,{Ms(Math.Max(3_500, offsetMs) - offsetMs)})*lt(t,{Ms(19_035 - offsetMs)})'", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clip_shorter_than_its_motion_slides_waits_on_its_first_frame()
    {
        var bump = ClipBump();

        var graph = Graph(FfmpegJobs.For(bump, Gumshoe, 0, bump.LengthMs, 0, clip: Clip with { LengthS = 4 })[0]);

        // 9.06 s of motion, a 4 s clip: frame 0 for 5.06 s, then 0-3.95 s of it, ending as the motion slides do.
        Assert.Contains("trim=start=0.000:end=3.950", graph, StringComparison.Ordinal);
        Assert.Contains("tpad=start_duration=5.060", graph, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flash", false, false, true)]
    [InlineData("duotone", false, true, false)]
    [InlineData("zoom", true, false, false)]
    [InlineData("duozoom", true, true, false)]
    public void Each_look_draws_its_own_hold_steps(string look, bool zooms, bool duotone, bool flashes)
    {
        var bump = ClipBump(look);

        var job = FfmpegJobs.For(bump, Gumshoe, 0, bump.LengthMs, 0, clip: Clip)[0];
        var graph = Graph(job);

        Assert.Equal(zooms, graph.Contains("zoompan", StringComparison.Ordinal));
        Assert.Equal(duotone, graph.Contains("colorchannelmixer", StringComparison.Ordinal));
        Assert.Equal(duotone, graph.Contains("negate=enable='gte(t,13.035)*lt(t,15.535)'", StringComparison.Ordinal)); // the odd step, inverted
        Assert.Equal(flashes, graph.Contains("drawbox", StringComparison.Ordinal));
        if (zooms)
        {
            Assert.Contains("z='if(gte(it,13.035)*lt(it,15.535),1.55,if(gte(it,9.060)*lt(it,13.035),1.25,1))'", graph, StringComparison.Ordinal);
        }

        // Words are hidden while the clip moves; over the clip they take the channel color, black with an outline on an
        // inverted step.
        Assert.DoesNotContain(job.TextFiles.Values, t => t.StartsWith("tonight's", StringComparison.Ordinal));
        Assert.Contains("fontcolor=0xE8DFC8:fontsize", graph, StringComparison.Ordinal);
        Assert.Equal(duotone, graph.Contains("fontcolor=0x000000:borderw=2:bordercolor=0xE8DFC8", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 1.25)]
    [InlineData(1, 1.55)]
    [InlineData(2, 1.9)]
    [InlineData(3, 2.3)]
    [InlineData(4, 2.3)]
    [InlineData(5, 1.25)] // the CSS stops at step 4, so later steps get step 0's scale
    public void Zoom_steps_follow_the_sites_css(int step, double scale) => Assert.Equal(scale, BumpClipVideo.Zoom(step));

    [Fact]
    public void Zoom_keeps_the_zoomAt_point_still()
    {
        var bump = ClipBump("zoom", "20% 75%");

        var graph = Graph(FfmpegJobs.For(bump, Gumshoe, 0, bump.LengthMs, 0, clip: Clip)[0]);

        Assert.Contains(":x='iw*0.200*(1-1/zoom)':y='ih*0.750*(1-1/zoom)':", graph, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("gpu")]
    public void Without_a_ready_clip_the_break_is_exactly_todays_and_a_clip_job_only_goes_in_front(string mode)
    {
        var picture = mode == "gpu" ? Picture.Gpu("/test/render-node") : Picture.Cpu;
        var bump = ClipBump();
        var plain = bump with { Clip = null };
        string Args(IEnumerable<FfmpegJob> jobs) => string.Join('\n', jobs.Select(j => string.Join(' ', j.Args)));

        var today = FfmpegJobs.For(plain, Gumshoe, 4_000, 16_035, 2, picture: picture);
        var notReady = FfmpegJobs.For(bump, Gumshoe, 4_000, 16_035, 2, picture: picture);
        var ready = FfmpegJobs.For(bump, Gumshoe, 4_000, 16_035, 2, picture: picture, clip: Clip);

        Assert.Equal(Args(today), Args(notReady));
        var clipJobs = mode == "gpu" ? 2 : 1;
        Assert.Equal(Args(today), Args(ready.Skip(clipJobs)));
        Assert.All(ready.Take(clipJobs), j => Assert.Contains(Clip.Path, j.Args));
    }

    [Fact]
    public async Task A_clip_job_that_fails_hands_the_rest_of_the_break_to_todays_bump()
    {
        var bump = ClipBump();
        var ran = new List<FfmpegJob>();

        var emitted = await SegmentPlayer.PlayAsync(bump, Gumshoe, 0, bump.LengthMs, 0, (job, _) =>
        {
            ran.Add(job);
            return Task.FromResult(ran.Count == 1 ? 6_000L : bump.LengthMs - 6_000);
        }, NullLogger.Instance, CancellationToken.None, clip: Clip);

        Assert.Equal(bump.LengthMs, emitted);
        Assert.Contains(Clip.Path, ran[0].Args);
        Assert.DoesNotContain(Clip.Path, ran[1].Args);
        Assert.Equal(Graph(FfmpegJobs.For(bump with { Clip = null }, Gumshoe, 6_000, bump.LengthMs - 6_000, 6)[0]), Graph(ran[1]));
    }

    [Fact]
    public async Task A_clip_is_only_offered_once_downloaded_and_measured_and_a_failure_is_not_retried_at_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spelunkr-clip-test-" + Guid.NewGuid().ToString("N"));
        var time = new ManualTime();
        var fail = true;
        var downloads = 0;
        var clips = new BumpClips(
            async (url, to, ct) =>
            {
                downloads++;
                if (fail)
                {
                    throw new HttpRequestException("503");
                }

                await to.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            },
            (path, ct) => Task.FromResult<double?>(12.5),
            dir,
            NullLogger.Instance,
            time);
        try
        {
            Assert.Null(clips.Ready(Src)); // not fetched yet: the break goes ahead without it
            await clips.Prefetch(Src);
            Assert.Null(clips.Ready(Src));
            fail = false;
            await clips.Prefetch(Src);
            Assert.Equal(1, downloads); // failed 0 minutes ago: not tried again yet

            time.Advance(TimeSpan.FromMinutes(11));
            await clips.Prefetch(Src);
            var ready = clips.Ready(Src)!;
            Assert.Equal(12.5, ready.LengthS);
            Assert.Equal(3, new FileInfo(ready.Path).Length);
            Assert.Empty(Directory.GetFiles(dir, "*.part"));
            Assert.Null(clips.Ready("https://archive.org/download/other/x.mp4"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
