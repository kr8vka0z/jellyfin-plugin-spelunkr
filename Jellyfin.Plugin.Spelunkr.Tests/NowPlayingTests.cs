// NowPlayingTests: what the cabinet's now-playing window says, and that its words are drawn onto the cabinet.
using Jellyfin.Plugin.Spelunkr.Schedule;
using Jellyfin.Plugin.Spelunkr.Stream;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class NowPlayingTests
{
    private static readonly ChannelLook Look = new("HOMEROOM", "#336699");
    private static readonly Segment Film = new() { Kind = "show", Url = "https://archive.org/download/x/x.mp4", Title = "His Girl Friday", Year = "1940" };

    [Fact]
    public void A_film_says_now_playing_with_its_own_title_and_year()
    {
        Assert.Equal(new NowPlaying("NOW PLAYING", "His Girl Friday", "1940"), NowPlaying.For(Film, null));
    }

    [Theory]
    [InlineData("bump")]
    [InlineData("ad")]
    [InlineData("up-next")]
    public void Between_films_it_says_up_next_with_the_next_film(string kind)
    {
        var between = new Segment { Kind = kind, Title = "Some tape", Year = "1999" };
        Assert.Equal(new NowPlaying("UP NEXT", "His Girl Friday", "1940"), NowPlaying.For(between, Film));
        Assert.Null(NowPlaying.For(between, null));
    }

    [Fact]
    public async Task The_next_film_is_found_in_the_real_schedule()
    {
        var engine = ScheduleEngineTests.GumshoeEngine();
        var bump = StreamPlanTests.OpenBump();
        var expected = engine.SegmentsBetween("gumshoe", bump.EndMs, bump.EndMs + 3_600_000, "https://audio.spelunkr.tv/bumps/").First(s => s.Kind == "show");

        var next = await NowPlaying.NextShowAsync(bump, (at, _) => Task.FromResult(engine.SegmentAt("gumshoe", at, "https://audio.spelunkr.tv/bumps/")), CancellationToken.None);

        Assert.NotNull(next);
        Assert.Equal((expected.StartMs, expected.Title), (next.StartMs, next.Title));
    }

    [Fact]
    public void A_film_with_no_year_has_no_year_line()
    {
        var job = FfmpegJobs.For(Film, Look, 0, 5_000, 0, null, Cabinet(Picture.Cpu, Film with { Year = null }))[0];
        Assert.Equal(["NOW PLAYING", "His Girl", "Friday"], WindowLines(job));
    }

    [Fact]
    public void A_long_title_wraps_onto_four_lines()
    {
        Assert.Equal(["The", "Adventures", "of Robin", "Hood"], new NowPlaying("NOW PLAYING", "The Adventures of Robin Hood", "1938").TitleLines());
    }

    [Fact]
    public void A_title_past_four_lines_ends_in_an_ellipsis_and_no_line_is_too_wide()
    {
        var np = new NowPlaying("UP NEXT", "The Private Life of the Gannets and Other Rare Seabirds of Scotland", null);
        var lines = np.TitleLines();

        Assert.Equal(4, lines.Count);
        Assert.EndsWith("…", lines[^1], StringComparison.Ordinal);
        Assert.Equal(NowPlaying.TitlePx, np.TitleSize);
        Assert.All(lines, l => Assert.True(NowPlaying.Ems(l) * np.TitleSize <= NowPlaying.LinePx, l));
    }

    [Fact]
    public void A_long_single_word_shrinks_the_title_to_fit_whole()
    {
        var np = new NowPlaying("NOW PLAYING", "Frankenstein", "1931");

        Assert.Equal(["Frankenstein"], np.TitleLines());
        Assert.True(np.TitleSize < NowPlaying.TitlePx);
        Assert.True(NowPlaying.Ems("Frankenstein") * np.TitleSize <= NowPlaying.LinePx);
    }

    [Fact]
    public void A_word_too_wide_for_the_window_is_broken_rather_than_spilling()
    {
        var np = new NowPlaying("NOW PLAYING", "WWWWWWWWWWWW", null);
        var lines = np.TitleLines();

        Assert.True(lines.Count > 1);
        Assert.Equal("WWWWWWWWWWWW", string.Concat(lines));
        Assert.All(lines, l => Assert.True(NowPlaying.Ems(l) * np.TitleSize <= NowPlaying.LinePx, l));
    }

    public static TheoryData<string> Modes => new() { "cpu", "gpu" };

    [Theory]
    [MemberData(nameof(Modes))]
    public void The_words_are_drawn_onto_the_cabinet_once_before_it_is_looped(string mode)
    {
        var picture = Cabinet(mode == "gpu" ? Picture.Gpu("/test/render-node") : Picture.Cpu, Film);
        var jobs = FfmpegJobs.For(Film, Look, 0, 5_000, 0, null, picture).Concat(FfmpegJobs.TuningIn(Film, Look, 4_000, picture));

        foreach (var job in jobs)
        {
            var filter = job.Args[job.Args.ToList().IndexOf("-filter_complex") + 1];
            var cabinet = filter[filter.IndexOf($"[{job.Args.Count(a => a == "-i") - 1}:v]", StringComparison.Ordinal)..];
            Assert.True(cabinet.IndexOf("textfile=np", StringComparison.Ordinal) is var text and > 0 && text < cabinet.IndexOf("loop=", StringComparison.Ordinal), filter);
            Assert.Equal(["NOW PLAYING", "His Girl", "Friday", "1940"], WindowLines(job));
        }
    }

    [Fact]
    public void The_window_sits_in_the_same_place_on_the_gpu_and_its_cpu_twin()
    {
        var jobs = FfmpegJobs.For(Film, Look, 0, 5_000, 0, null, Cabinet(Picture.Gpu("/test/render-node"), Film));
        string Window(FfmpegJob j) => string.Join(",", j.Args[j.Args.ToList().IndexOf("-filter_complex") + 1].Split(',').Where(p => p.Contains("textfile=np", StringComparison.Ordinal)));

        Assert.Equal(Window(jobs[0]), Window(jobs[1]));
    }

    [Fact]
    public void No_cabinet_means_no_window()
    {
        foreach (var job in FfmpegJobs.For(Film, Look, 0, 5_000, 0, null, Picture.Cpu with { Window = NowPlaying.For(Film, null) }))
        {
            Assert.DoesNotContain(job.Args, a => a.Contains("textfile=np", StringComparison.Ordinal));
            Assert.DoesNotContain(job.TextFiles.Keys, k => k.StartsWith("np", StringComparison.Ordinal));
        }
    }

    private static Picture Cabinet(Picture p, Segment film) => p with { Cabinet = "/test/cabinet.png", Window = NowPlaying.For(film, null) };

    // The window's text files in the order they are drawn.
    private static List<string> WindowLines(FfmpegJob job) =>
        job.TextFiles.Where(f => f.Key.StartsWith("np", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Value).ToList();
}
