// SpelunkrSourceTests: the data layer reads Spelunkr's lineup the way the live bucket serves it (here: the
// fixture mirror), offers only the enabled channels, and loads their schedules.
using Jellyfin.Plugin.Spelunkr.Schedule;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class SpelunkrSourceTests
{
    private static SpelunkrSource FromFixtures(List<string>? reads = null) => new(
        (path, _) =>
        {
            reads?.Add(path);
            return Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path)));
        },
        NullLogger<SpelunkrSource>.Instance);

    [Fact]
    public async Task Offers_only_gumshoe_with_its_dial_number_and_color()
    {
        var channels = await FromFixtures().GetChannelsAsync(CancellationToken.None);

        var gumshoe = Assert.Single(channels);
        Assert.Equal("gumshoe", gumshoe.Id);
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
            "\"versions\":[{\"v\":19",
            $"\"versions\":[{{\"v\":20,\"manifest\":\"manifests/gumshoe/v20.json\",\"epochMs\":{takeover},\"effectiveAtMs\":{takeover},\"startIndex\":0}},{{\"v\":19",
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

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
