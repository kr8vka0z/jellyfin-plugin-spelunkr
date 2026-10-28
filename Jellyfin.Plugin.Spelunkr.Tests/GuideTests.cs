// GuideTests: the TV guide shows each film from its start until the next film starts (breaks folded in),
// with ids that stay the same between guide refreshes.
using Jellyfin.Plugin.Spelunkr.Guide;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class GuideTests
{
    private static IReadOnlyList<Segment> Day() =>
        ScheduleEngineTests.GumshoeEngine().SegmentsBetween("gumshoe", 1_793_217_600_000, 1_793_217_600_000 + (24 * 3_600_000L), "x/");

    [Fact]
    public void Films_run_until_the_next_film_with_breaks_folded_in()
    {
        var programs = GuidePrograms.Build(Day(), "spelunkr_gumshoe");

        var first = programs[0];
        Assert.Equal("The Long Night", first.Name);
        Assert.Equal(1947, first.ProductionYear);
        Assert.True(first.IsMovie);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_793_215_002_819).UtcDateTime, first.StartDate);
        // Ends where "Forbidden" starts, so the bump, ad and bump between them belong to The Long Night.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_793_220_913_459).UtcDateTime, first.EndDate);
        Assert.Equal("Forbidden", programs[1].Name);
        Assert.Equal(19, programs.Count); // 19 films start in this fixture window
        for (var i = 1; i < programs.Count; i++)
        {
            Assert.Equal(programs[i - 1].EndDate, programs[i].StartDate);
        }
    }

    [Fact]
    public void Program_ids_are_stable_across_refreshes()
    {
        var a = GuidePrograms.Build(Day(), "spelunkr_gumshoe");
        var b = GuidePrograms.Build(Day(), "spelunkr_gumshoe");

        Assert.Equal(a.Select(p => p.Id), b.Select(p => p.Id));
        Assert.Equal("spelunkr_gumshoe-19-1-172", a[0].Id);
        Assert.Equal(a.Count, a.Select(p => p.Id).Distinct().Count());
    }
}
