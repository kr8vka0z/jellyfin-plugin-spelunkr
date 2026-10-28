// SpelunkrSourceTests: the data layer reads Spelunkr's lineup the way the live bucket serves it (here: the
// fixture mirror), offers the channels spelunkr.tv lists, and loads their schedules.
using Jellyfin.Plugin.Spelunkr.Schedule;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class SpelunkrSourceTests
{
    internal static SpelunkrSource FromFixtures(List<string>? reads = null) => new(
        (path, _) =>
        {
            reads?.Add(path);
            return Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path)));
        },
        NullLogger<SpelunkrSource>.Instance);

    [Fact]
    public void Offers_every_numbered_channel_that_is_not_coming_soon_debug_or_audio_overlay()
    {
        var lineup = System.Text.Json.JsonSerializer.Deserialize<Lineup>(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "lineup.json")), ScheduleEngine.Json)!;

        var left = lineup.Channels.Where(c => !SpelunkrSource.IsOffered(c)).Select(c => c.Id).Order();

        // rocket-punch: debug only; oops-all-bumps and test-pattern: no number (debug in this saved lineup);
        // vacation-slides: its music overlay isn't built yet.
        Assert.Equal(["oops-all-bumps", "rocket-punch", "test-pattern", "vacation-slides"], left);
        Assert.Equal(30, lineup.Channels.Count(SpelunkrSource.IsOffered));
    }

    [Fact]
    public async Task A_channel_whose_files_fail_to_load_is_left_out_without_stopping_the_rest()
    {
        // The fixture mirror only holds gumshoe's and homeroom's manifests; every other channel's fetch fails.
        var channels = await FromFixtures().GetChannelsAsync(CancellationToken.None);

        Assert.Equal(["gumshoe", "homeroom"], channels.Select(c => c.Id).Order());
        var gumshoe = channels.Single(c => c.Id == "gumshoe");
        Assert.Equal("GUMSHOE", gumshoe.Name);
        Assert.Equal(101, gumshoe.Dial);
        Assert.Equal("#E8DFC8", gumshoe.Color);
    }

    [Fact]
    public async Task Answers_what_is_on_and_reads_the_lineup_once_an_hour()
    {
        var reads = new List<string>();
        var source = FromFixtures(reads);

        var seg = await source.SegmentAtAsync("gumshoe", 1_793_000_000_000, CancellationToken.None);
        await source.SegmentAtAsync("gumshoe", 1_793_000_100_000, CancellationToken.None);

        Assert.Equal("D.o.a.VideoQualityUpgrade", seg.Id);
        Assert.Equal(1, reads.Count(p => p == "lineup.json"));
        Assert.Contains("platform/294b0bf5ae4d1917.json", reads); // the network-wide bumps file, needed for timing
    }

    [Fact]
    public async Task A_new_version_in_the_hourly_lineup_takes_over_and_refreshes_the_guide()
    {
        // Spelunkr publishes a new version ahead of time; from its effectiveAtMs the schedule switches to it.
        // Simulated with a copy of v19 relabelled v20, taking over at 2026-10-31T00:00Z.
        const long takeover = 1_793_404_800_000;
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_000));
        var lineupV19 = File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "lineup.json"));
        var lineupV20 = lineupV19.Replace(
            "\"versions\":[{\"v\":19,\"manifest\":\"manifests/gumshoe/",
            $"\"versions\":[{{\"v\":20,\"manifest\":\"manifests/gumshoe/v20.json\",\"epochMs\":{takeover},\"effectiveAtMs\":{takeover},\"startIndex\":0}},{{\"v\":19,\"manifest\":\"manifests/gumshoe/",
            StringComparison.Ordinal);
        Assert.NotEqual(lineupV19, lineupV20); // the fixture still has the shape the Replace expects
        var published = false;
        var guideRefreshes = 0;
        var source = new SpelunkrSource(
            (path, _) => Task.FromResult(path switch
            {
                "lineup.json" => published ? lineupV20 : lineupV19,
                "manifests/gumshoe/v20.json" => File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "manifests/gumshoe/v19.json")).Replace("\"version\":19", "\"version\":20", StringComparison.Ordinal),
                _ => File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path)),
            }),
            NullLogger<SpelunkrSource>.Instance,
            () => guideRefreshes++,
            clock);

        Assert.Equal(19, (await source.SegmentAtAsync("gumshoe", takeover + 60_000, CancellationToken.None)).Version);
        Assert.Equal(0, guideRefreshes); // the first load isn't news; Registration queues the first guide refresh

        published = true;
        clock.Now += TimeSpan.FromMinutes(61);
        var after = await source.SegmentAtAsync("gumshoe", takeover + 60_000, CancellationToken.None);
        var before = await source.SegmentAtAsync("gumshoe", takeover - 60_000, CancellationToken.None);

        Assert.Equal(20, after.Version);
        Assert.Equal(19, before.Version);
        Assert.Equal(1, guideRefreshes);
    }

    [Theory]
    [InlineData("manifests/gumshoe/v19.json#/../../../.github/workflows/build.yml")]
    [InlineData("manifests/../../secrets.json")]
    [InlineData("/etc/passwd")]
    [InlineData("platform/x.json?y=1")]
    [InlineData("https://elsewhere.example/x.json")]
    public async Task Refuses_data_paths_that_leave_the_bucket(string badPath)
    {
        // A manifest or platform path comes from Spelunkr's data and is appended to the bucket URL.
        var lineup = File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "lineup.json"))
            .Replace("\"manifests/gumshoe/v19.json\"", System.Text.Json.JsonSerializer.Serialize(badPath), StringComparison.Ordinal);
        var reads = new List<string>();
        var source = new SpelunkrSource(
            (path, _) =>
            {
                reads.Add(path);
                return path == "lineup.json" ? Task.FromResult(lineup) : throw new FileNotFoundException(path);
            },
            NullLogger<SpelunkrSource>.Instance);

        var channels = await source.GetChannelsAsync(CancellationToken.None);

        Assert.DoesNotContain(badPath, reads); // never fetched
        Assert.DoesNotContain(channels, c => c.Id == "gumshoe"); // and the channel is left out
    }

    [Fact]
    public async Task Refuses_a_platform_path_that_leaves_the_bucket()
    {
        Func<string, CancellationToken, Task<string>> source = (path, _) => path switch
            {
                "manifests/gumshoe/v19.json" => Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path))
                    .Replace("\"platform/294b0bf5ae4d1917.json\"", "\"platform/../../x.json\"", StringComparison.Ordinal)),
                "lineup.json" => Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path))),
                _ => throw new FileNotFoundException(path),
            };
        var reads = new List<string>();

        var channels = await new SpelunkrSource(
            (path, ct) =>
            {
                reads.Add(path);
                return source(path, ct);
            },
            NullLogger<SpelunkrSource>.Instance).GetChannelsAsync(CancellationToken.None);

        Assert.DoesNotContain(reads, p => p.Contains("..", StringComparison.Ordinal));
        Assert.DoesNotContain(channels, c => c.Id == "gumshoe");
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("/etc/passwd")]
    [InlineData("Gumshoe")]
    [InlineData("a/b")]
    public void Channels_with_ids_that_are_not_one_plain_name_are_not_offered(string id)
    {
        Assert.False(SpelunkrSource.IsOffered(new LineupChannel(id, "X", "#000000", 1, [])));
        Assert.True(SpelunkrSource.IsOffered(new LineupChannel("late-nite-local", "X", "#000000", 11, [])));
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("/tmp/rooted")]
    public void Logo_files_stay_in_the_logo_folder_whatever_the_channel_id(string id)
    {
        var dir = Directory.CreateTempSubdirectory("spelunkr-logos").FullName;
        try
        {
            var channel = new LineupChannel(id, "X", "#E8DFC8", 1, [], Logomark: new Logomark("pixel", ["c."]));

            var path = SpelunkrTunerHost.LogoFile(dir, channel, NullLogger.Instance);

            Assert.Equal(dir, Path.GetDirectoryName(path));
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_fetch_that_times_out_only_costs_that_channel_or_its_descriptions()
    {
        // HttpClient reports its own timeout as TaskCanceledException while the caller's token is still live.
        var source = new SpelunkrSource(
            (path, _) => path is "manifests/homeroom/v21.json" or "details/gumshoe/v19.json"
                ? throw new TaskCanceledException("timed out", new TimeoutException())
                : Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path))),
            NullLogger<SpelunkrSource>.Instance);

        var channels = await source.GetChannelsAsync(CancellationToken.None);
        var film = await source.SegmentAtAsync("gumshoe", 1_793_215_002_819, CancellationToken.None);

        Assert.Equal(["gumshoe"], channels.Select(c => c.Id)); // homeroom's manifest timed out; gumshoe unaffected
        Assert.Null(source.Details("gumshoe", film)); // its details timed out: titles only
    }

    [Fact]
    public async Task A_real_cancellation_still_stops_the_load()
    {
        using var cts = new CancellationTokenSource();
        var source = new SpelunkrSource(
            (path, ct) =>
            {
                if (path.StartsWith("manifests/", StringComparison.Ordinal))
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                return Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path)));
            },
            NullLogger<SpelunkrSource>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetChannelsAsync(cts.Token));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
