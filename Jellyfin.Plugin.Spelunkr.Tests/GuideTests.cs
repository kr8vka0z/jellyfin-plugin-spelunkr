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
    public async Task A_window_starting_and_ending_in_breaks_keeps_both_boundary_films_whole()
    {
        // Starts in the bump after The Long Night (film ended 1793220818819), ends in the bump after Forbidden
        // (film ended 1793225961459). Fixture: Fixtures/timeline-1793217600000.jsonl.
        var guide = new SpelunkrGuide(SpelunkrSourceTests.FromFixtures());
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1_793_220_823_819).UtcDateTime;
        var end = DateTimeOffset.FromUnixTimeMilliseconds(1_793_225_970_000).UtcDateTime;

        var programs = (await guide.GetProgramsAsync(new(), "spelunkr_gumshoe", start, end, CancellationToken.None)).ToList();

        Assert.Equal(["The Long Night", "Forbidden"], programs.Select(p => p.Name));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_793_215_002_819).UtcDateTime, programs[0].StartDate);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_793_220_913_459).UtcDateTime, programs[0].EndDate);
        // Forbidden runs until Shadow of Terror starts, past the end of the window.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_793_226_108_541).UtcDateTime, programs[1].EndDate);
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
