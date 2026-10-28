// StreamPlanTests: how a schedule segment becomes ffmpeg work. Covers the parts that decide what the viewer sees
// when: joining a film mid-way, bump slide timing, ad volume, and keeping the stream on the wall clock.
using System.Globalization;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Jellyfin.Plugin.Spelunkr.Stream;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class StreamPlanTests
{
    private static readonly ChannelLook Gumshoe = new("GUMSHOE", "#E8DFC8");

    // An "open" bump straight from the oracle (Fixtures/timeline-1793217600000.jsonl, index 173).
    private static Segment OpenBump() => ScheduleEngineTests.GumshoeEngine()
        .SegmentsBetween("gumshoe", 1_793_220_818_819, 1_793_220_818_820, "https://audio.spelunkr.tv/bumps/")[0];

    private static string Arg(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void Open_bump_shows_the_channel_name_during_the_lead_in_then_each_slide_in_turn()
    {
        var bump = OpenBump();
        Assert.Equal("open", bump.Half);
        Assert.Equal(20_035, bump.LengthMs);

        var cards = BumpCards.For(bump, Gumshoe);

        Assert.Equal(new BumpCard(0, 3_500, "GUMSHOE"), cards[0]);
        Assert.Equal(new BumpCard(3_500, 6_375, "tonight's forecast: dark."), cards[1]); // 2875 ms slide
        Assert.Equal(20_035 - 2_000, cards[^1].EndMs); // a 2 s tail with no text closes the bump
        for (var i = 1; i < cards.Count; i++)
        {
            Assert.Equal(cards[i - 1].EndMs, cards[i].StartMs);
        }
    }

    [Fact]
    public void Full_bump_puts_the_network_slides_after_the_channel_part_tail_and_gap()
    {
        var full = new Segment
        {
            Kind = "bump",
            Half = "full",
            LengthMs = 3_500 + 2_500 + 2_000 + 300 + 3_000 + 2_000,
            Channel = new BumpPart("gs-1", [new Slide("gumshoe.", 2_500)], null),
            Platform = new BumpPart("sp-1", [new Slide("spelunkr.tv", 3_000)], null),
            Layout = new BumpLayout(3_500, 2_000, 300),
        };

        var cards = BumpCards.For(full, Gumshoe);

        Assert.Equal(new BumpCard(3_500, 6_000, "gumshoe."), cards[1]);
        Assert.Equal(new BumpCard(8_300, 11_300, "spelunkr.tv"), cards[2]);
    }

    [Fact]
    public void Film_joined_mid_way_seeks_into_the_file_and_runs_to_the_segment_end()
    {
        var show = new Segment { Kind = "show", Url = "https://archive.org/download/x/x.mp4", InMs = 1_000, Title = "X" };

        var job = FfmpegJobs.For(show, Gumshoe, offsetMs: 600_000, durationMs: 8_000, outputOffsetS: 12.5)[0];

        Assert.Equal("601.000", Arg(job.Args, "-ss"));
        Assert.Equal("https://archive.org/download/x/x.mp4", Arg(job.Args, "-i"));
        Assert.Equal("8.000", Arg(job.Args, "-t"));
        Assert.Equal("12.500", Arg(job.Args, "-output_ts_offset"));
        Assert.Equal("pipe:1", job.Args[^1]);
    }

    [Fact]
    public void Ads_get_their_volume_correction()
    {
        var ad = new Segment { Kind = "ad", Url = "https://archive.org/download/t/t.mp4", GainDb = -0.8 };

        var job = FfmpegJobs.For(ad, Gumshoe, 0, 60_000, 0)[0];

        Assert.Contains("volume=-0.8dB", Arg(job.Args, "-af"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_kind_falls_back_to_a_title_slate_so_the_stream_never_stops()
    {
        var show = new Segment { Kind = "show", Url = "https://archive.org/download/x/x.mp4", Title = "D.O.A." };

        var jobs = FfmpegJobs.For(show, Gumshoe, 0, 5_000, 0);

        var slate = jobs[^1];
        Assert.DoesNotContain(slate.Args, a => a.StartsWith("https://", StringComparison.Ordinal));
        Assert.Contains("D.O.A.", slate.TextFiles.Values);
        Assert.Equal("5.000", Arg(slate.Args, "-t"));
    }

    [Fact]
    public void Bump_joined_part_way_shifts_slides_and_music()
    {
        var bump = OpenBump();

        var job = FfmpegJobs.For(bump, Gumshoe, offsetMs: 4_000, durationMs: bump.LengthMs - 4_000, outputOffsetS: 0)[0];

        // Slide 2 ran 3.5-6.375 s into the bump; joined at 4 s it shows from 0 to 2.375 s of this job.
        var vf = Arg(job.Args, "-filter_complex");
        Assert.Contains("enable='gte(t,-0.500)*lt(t,2.375)'", vf, StringComparison.Ordinal);
        var musicStart = double.Parse(Arg(job.Args, "-ss"), CultureInfo.InvariantCulture);
        Assert.Equal(bump.Channel!.Audio!.StartOffsetS + 4, musicStart, 3);
    }

    [Theory]
    [InlineData(100_000, 100_000, 100_000, 0)] // on time: go straight on
    [InlineData(80_000, 100_000, 100_000, 0)] // 20 s behind (slow network): skip ahead to now
    [InlineData(95_000, 100_000, 95_000, 0)] // 5 s behind: fine, keep the content continuous
    [InlineData(140_000, 100_000, 140_000, 10_000)] // 40 s ahead: wait until only 30 s ahead
    public void Pacing_keeps_the_stream_on_the_wall_clock(long contentMs, long wallMs, long expectContentMs, long expectWaitMs)
    {
        var (next, wait) = Pacing.Next(contentMs, wallMs);

        Assert.Equal(expectContentMs, next);
        Assert.Equal(expectWaitMs, wait);
    }
}
