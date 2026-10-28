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
}
