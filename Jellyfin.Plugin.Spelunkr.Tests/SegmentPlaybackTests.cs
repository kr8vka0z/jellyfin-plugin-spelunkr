// SegmentPlaybackTests: what happens when ffmpeg fails part-way, fails completely, or hangs. The stream must
// fill the rest of the segment with the next fallback on continuous timestamps, and never wait forever.
using System.Globalization;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Jellyfin.Plugin.Spelunkr.Stream;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class SegmentPlaybackTests
{
    private static readonly ChannelLook Look = new("GUMSHOE", "#E8DFC8");
    private static readonly Segment Film = new() { Kind = "show", Url = "https://archive.org/download/x/x.mp4", Title = "X" };

    private static string Arg(FfmpegJob job, string flag) => job.Args[job.Args.ToList().IndexOf(flag) + 1];

    [Fact]
    public async Task Film_that_dies_part_way_is_finished_by_the_next_fallback_from_where_it_stopped()
    {
        var ran = new List<FfmpegJob>();
        var emitted = await SegmentPlayer.PlayAsync(Film, Look, offsetMs: 1_000, durationMs: 60_000, outputOffsetS: 100, (job, _) =>
        {
            ran.Add(job);
            return Task.FromResult(ran.Count == 1 ? 20_000L : long.Parse(Arg(job, "-t").Replace(".", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture));
        }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(60_000, emitted);
        Assert.Equal(2, ran.Count);
        // The second job is the film-with-silence fallback, picking up 20 s later in the file and in the stream.
        Assert.Equal("21.000", Arg(ran[1], "-ss"));
        Assert.Equal("40.000", Arg(ran[1], "-t"));
        Assert.Equal("120.000", Arg(ran[1], "-output_ts_offset"));
        Assert.Contains("anullsrc=r=48000:cl=stereo", ran[1].Args);
    }

    [Fact]
    public async Task When_every_fallback_fails_it_reports_what_was_actually_emitted()
    {
        var ran = 0;
        var emitted = await SegmentPlayer.PlayAsync(Film, Look, 0, 60_000, 0, (_, _) =>
        {
            ran++;
            return Task.FromResult(0L);
        }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, emitted);
        Assert.Equal(3, ran); // film, film with silence, title slate: each tried once, then give up
    }

    [Fact]
    public async Task Progress_reports_set_the_emitted_length()
    {
        using var dir = new TempDir();
        using var output = new MemoryStream();
        var job = new FfmpegJob(["-c", "echo out_time_us=1500000 >&2; printf abc; echo out_time_us=2500000 >&2"], new Dictionary<string, string>());

        var emitted = await FfmpegProcess.RunAsync("/bin/sh", job, dir.Path, output, () => { }, TimeSpan.FromSeconds(10), NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2_500, emitted);
        Assert.Equal("abc"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public async Task A_job_that_stops_sending_is_killed()
    {
        using var dir = new TempDir();
        using var output = new MemoryStream();
        var job = new FfmpegJob(["-c", "echo out_time_us=1000000 >&2; printf x; sleep 30"], new Dictionary<string, string>());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var emitted = await FfmpegProcess.RunAsync("/bin/sh", job, dir.Path, output, () => { }, TimeSpan.FromSeconds(1), NullLogger.Instance, CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal(1_000, emitted);
    }

    [Fact]
    public void Every_remote_input_has_a_read_timeout()
    {
        var bump = StreamPlanTests.OpenBump();
        var jobs = FfmpegJobs.For(Film, Look, 0, 5_000, 0).Concat(FfmpegJobs.For(bump, Look, 0, 5_000, 0));

        foreach (var job in jobs)
        {
            var args = job.Args.ToList();
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] == "-i" && args[i + 1].StartsWith("http", StringComparison.Ordinal))
                {
                    var before = args.Take(i).ToList();
                    var lastInput = before.LastIndexOf("-i");
                    Assert.Contains("-rw_timeout", before.Skip(lastInput + 1));
                }
            }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("spelunkr-test").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }
}
