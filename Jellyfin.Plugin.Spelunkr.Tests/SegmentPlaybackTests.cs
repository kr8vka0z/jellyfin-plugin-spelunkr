// SegmentPlaybackTests: what happens when ffmpeg fails part-way, fails completely, or hangs. The stream must
// fill the rest of the segment with the next fallback on continuous timestamps, and never wait forever.
using System.Globalization;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Jellyfin.Plugin.Spelunkr.Stream;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Spelunkr.Tests.TestKit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class SegmentPlaybackTests
{
    private static readonly ChannelLook Look = new("GUMSHOE", "#E8DFC8");
    private static readonly Segment Film = new() { Kind = "show", Url = "https://archive.org/download/x/x.mp4", Title = "X" };

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
    public async Task Gpu_film_that_dies_part_way_is_finished_by_the_same_film_on_the_cpu_not_the_slate()
    {
        // The upscaler (libplacebo) breaks after 20 s while plain GPU encoding still works: the slate's GPU job
        // would succeed, so it must not come before the film's CPU twin.
        var ran = new List<FfmpegJob>();
        var emitted = await SegmentPlayer.PlayAsync(Film, Look, 0, 60_000, 0, (job, _) =>
        {
            ran.Add(job);
            var upscaled = job.Args.Any(a => a.Contains("libplacebo", StringComparison.Ordinal));
            return Task.FromResult(upscaled ? (ran.Count == 1 ? 20_000L : 0L) : long.Parse(Arg(job, "-t").Replace(".", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture));
        }, NullLogger.Instance, CancellationToken.None, picture: Picture.Gpu("/test/render-node"));

        Assert.Equal(60_000, emitted);
        Assert.Equal(2, ran.Count);
        Assert.Equal("libx264", Arg(ran[1], "-c:v"));
        Assert.Equal(Film.Url, Arg(ran[1], "-i"));
        Assert.Equal("20.000", Arg(ran[1], "-ss"));
        Assert.Equal("20.000", Arg(ran[1], "-output_ts_offset"));
    }

    [Theory]
    [InlineData(40_000, true)] // played 30 s, then the download dropped: the same film resumes
    [InlineData(11_000, false)] // died after 1 s and was padded 10 s: no real progress, so the next fallback
    public async Task A_film_that_played_and_then_stopped_is_resumed_not_replaced(long firstMs, bool resumed)
    {
        var ran = new List<FfmpegJob>();
        var emitted = await SegmentPlayer.PlayAsync(Film, Look, 1_000, 600_000, 100, (job, _) =>
        {
            ran.Add(job);
            return Task.FromResult(ran.Count == 1 ? firstMs : AskedMs(job));
        }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, ran.Count);
        Assert.Equal(resumed, !ran[1].StandIn);
        // Either way the next job picks up where the first one stopped, in the file and in the stream.
        Assert.Equal((1_000 + firstMs) / 1000.0, double.Parse(Arg(ran[1], "-ss"), CultureInfo.InvariantCulture));
        Assert.Equal(100 + (firstMs / 1000.0), double.Parse(Arg(ran[1], "-output_ts_offset"), CultureInfo.InvariantCulture));
        // A stand-in runs a minute and hands back, so the real film is tried again soon.
        Assert.Equal(resumed ? 600_000 : firstMs + SegmentPlayer.StandInMs, emitted);
    }

    [Fact]
    public async Task A_film_that_keeps_dropping_is_resumed_a_few_times_then_stands_in_for_a_minute()
    {
        var ran = new List<FfmpegJob>();
        var emitted = await SegmentPlayer.PlayAsync(Film, Look, 0, 600_000, 0, (job, _) =>
        {
            ran.Add(job);
            return Task.FromResult(job.StandIn ? AskedMs(job) : 30_000L);
        }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(SegmentPlayer.MaxRetries + 1, ran.Count(j => !j.StandIn));
        Assert.Equal("60.000", Arg(ran[^1], "-t"));
        Assert.Equal(((SegmentPlayer.MaxRetries + 1) * 30_000) + SegmentPlayer.StandInMs, emitted);
    }

    [Theory]
    [InlineData(0, 1)] // never played: set aside
    [InlineData(40_000, 0)] // played, then dropped and wouldn't come back: kept for next time
    public async Task An_original_is_set_aside_only_when_it_never_played(long firstMs, int expectFailures)
    {
        const string original = "https://archive.org/download/x/original.mp4";
        var urls = new List<string>();
        var failures = 0;

        await SegmentPlayer.PlayAsync(Film, Look, 0, 600_000, 0, (job, _) =>
        {
            urls.Add(Arg(job, "-i"));
            return Task.FromResult(urls.Count == 1 ? firstMs : urls[^1] == original ? 0L : AskedMs(job));
        }, NullLogger.Instance, CancellationToken.None, original, originalFailed: () => failures++);

        Assert.Equal(Film.Url, urls[^1]);
        Assert.Equal(expectFailures, failures);
    }

    [Fact]
    public async Task A_source_down_at_the_start_gets_a_minute_of_slate_then_the_film_is_tried_again()
    {
        var segment = Film with { StartMs = 0, LengthMs = 600_000 };
        var ran = new List<FfmpegJob>();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StreamPump.RunAsync(
            0,
            () => 0,
            (_, _) => Task.FromResult(segment),
            (s, offsetMs, durationMs, outputS, c) => SegmentPlayer.PlayAsync(s, Look, offsetMs, durationMs, outputS, (job, _) =>
            {
                ran.Add(job);
                if (ran.Count == 4)
                {
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                }

                // The film and the film with silence fail at once (the source is down); the slate works.
                return Task.FromResult(job.Args.Contains(Film.Url) ? 0L : AskedMs(job));
            }, NullLogger.Instance, c),
            (_, _, _) => Task.FromResult(0L),
            (_, _) => Task.CompletedTask,
            cts.Token));

        Assert.Equal(4, ran.Count);
        Assert.Equal("60.000", Arg(ran[2], "-t"));
        Assert.DoesNotContain(Film.Url, ran[2].Args);
        // A minute later the real film comes first again, a minute into the film and the stream.
        Assert.Equal((Film.Url, "60.000", "60.000"), (Arg(ran[3], "-i"), Arg(ran[3], "-ss"), Arg(ran[3], "-output_ts_offset")));
        Assert.False(ran[3].StandIn);
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

        var emitted = await FfmpegProcess.RunAsync("/bin/sh", job, dir.Path, output, () => { }, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), NullLogger.Instance, CancellationToken.None);

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

        var emitted = await FfmpegProcess.RunAsync("/bin/sh", job, dir.Path, output, () => { }, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), NullLogger.Instance, CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal(1_000, emitted);
    }

    [Fact]
    public async Task A_job_that_never_starts_gets_the_shorter_connect_timeout()
    {
        using var dir = new TempDir();
        using var output = new MemoryStream();
        var job = new FfmpegJob(["-c", "sleep 30"], new Dictionary<string, string>());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var emitted = await FfmpegProcess.RunAsync("/bin/sh", job, dir.Path, output, () => { }, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), NullLogger.Instance, CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal(0, emitted);
    }

    [Fact]
    public async Task Once_started_a_job_keeps_the_longer_stall_timeout()
    {
        using var dir = new TempDir();
        using var output = new MemoryStream();
        var job = new FfmpegJob(["-c", "printf a; sleep 2; echo out_time_us=2000000 >&2; printf b"], new Dictionary<string, string>());

        var emitted = await FfmpegProcess.RunAsync("/bin/sh", job, dir.Path, output, () => { }, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2_000, emitted);
        Assert.Equal("ab"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public async Task A_dead_original_goes_straight_to_spelunkrs_file_and_is_reported()
    {
        const string original = "https://archive.org/download/x/original.mp4";
        var urls = new List<string>();
        var failures = 0;

        var emitted = await SegmentPlayer.PlayAsync(Film, Look, 0, 60_000, 0, (job, _) =>
        {
            urls.Add(Arg(job, "-i"));
            return Task.FromResult(urls.Count == 1 ? 0L : 60_000L);
        }, NullLogger.Instance, CancellationToken.None, original, Picture.Gpu("/dev/dri/renderD128"), () => failures++);

        Assert.Equal(60_000, emitted);
        Assert.Equal([original, Film.Url!], urls); // not the original again on the CPU
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task A_gpu_tuning_in_card_that_fails_is_drawn_on_the_cpu_and_touches_no_network()
    {
        var jobs = new List<FfmpegJob>();

        var emitted = await SegmentPlayer.PlayTuningInAsync(Film, Look, 60_000, (job, _) =>
        {
            jobs.Add(job);
            return Task.FromResult(jobs.Count == 1 ? 0L : 4_000L);
        }, CancellationToken.None, Picture.Gpu("/dev/dri/renderD128"));

        Assert.Equal(4_000, emitted);
        Assert.Equal(2, jobs.Count);
        Assert.Contains("h264_vaapi", jobs[0].Args);
        Assert.Contains("libx264", jobs[1].Args);
        Assert.All(jobs, j => Assert.DoesNotContain(j.Args, a => a.Contains("http", StringComparison.Ordinal)));
        Assert.Contains(Film.Title!, jobs[0].TextFiles.Values.Single());
        Assert.Equal("4.000", Arg(jobs[0], "-t"));
    }

    [Fact]
    public void Every_remote_input_has_a_read_timeout()
    {
        var bump = OpenBump();
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
}
