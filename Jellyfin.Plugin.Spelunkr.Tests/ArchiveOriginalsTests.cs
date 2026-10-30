// ArchiveOriginalsTests: when the stream swaps Spelunkr's compressed archive.org file for the item's original.
// A wrong swap shows a different cut of the film (off schedule) or stutters, so every rule that can refuse it
// is pinned here, plus the lookup never holding up tune-in or failing the stream.
using System.Text.Json;
using Jellyfin.Plugin.Spelunkr.Stream;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class ArchiveOriginalsTests
{
    private const string Item = "0013_How_to_Use_Classroom_Films_E00474_13_29_45_00";
    private const string Derivative = Item + ".mp4";
    private static readonly string Url = $"https://archive.org/download/{Item}/{Derivative}";

    // Shaped like archive.org's metadata API for the example item (sizes and lengths from the live response).
    private static string Metadata(object original, string derivativeSource = "derivative", string? originalRef = Item + ".m4v")
        => JsonSerializer.Serialize(new
        {
            files = new object[]
            {
                original,
                new { name = Derivative, source = derivativeSource, format = "h.264", length = "867.91", size = "89664316", original = originalRef },
            },
        });

    private static object M4v(object length, object size, string name = Item + ".m4v") => new { name, source = "original", format = "MPEG4", length, size };

    public static TheoryData<string, string, string?> Cases => new()
    {
        { "same film, ~3 Mbps original", Metadata(M4v("867.9", "343411932")), Item + ".m4v" },
        { "lengths as numbers", Metadata(M4v(867.9, 343411932)), Item + ".m4v" },
        { "length as h:m:s", Metadata(M4v("00:14:27.9", "343411932")), Item + ".m4v" },
        { "a different cut (5 s longer)", Metadata(M4v("872.9", "343411932")), null },
        { "too heavy to stream (~12 Mbps)", Metadata(M4v("867.9", "1302000000")), null },
        { "not a seekable MP4-family file", Metadata(M4v("867.9", "343411932", Item + ".mpeg"), originalRef: Item + ".mpeg"), null },
        { "Spelunkr already links the original", Metadata(M4v("867.9", "343411932"), derivativeSource: "original"), null },
        { "derivative names no original", Metadata(M4v("867.9", "343411932"), originalRef: null), null },
        { "named original missing from the list", Metadata(M4v("867.9", "343411932", "other.m4v")), null },
        { "unreadable length", Metadata(M4v("about 14 min", "343411932")), null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Picks_the_original_only_when_it_is_the_same_film_and_streams_smoothly(string because, string metadata, string? expected)
    {
        var picked = ArchiveOriginals.Pick(metadata, Derivative);
        Assert.True(expected == picked, $"{because}: expected {expected ?? "none"}, got {picked ?? "none"}");
    }

    [Fact]
    public async Task A_rejected_original_stays_rejected_even_if_a_lookup_was_already_under_way()
    {
        var lookup = new TaskCompletionSource<string>();
        var originals = new ArchiveOriginals((_, _) => lookup.Task, NullLogger.Instance, TimeProvider.System, TimeSpan.FromMinutes(1));

        var underWay = originals.FindAsync(Url, CancellationToken.None);
        originals.Reject(Url);
        lookup.SetResult(Metadata(M4v("867.9", "343411932")));

        Assert.Null(await underWay);
        Assert.Null(await originals.FindAsync(Url, CancellationToken.None));
    }

    [Fact]
    public async Task Builds_an_escaped_download_url_for_the_original()
    {
        var withSpace = JsonSerializer.Serialize(new
        {
            files = new object[]
            {
                M4v("867.9", "343411932", "Reel 1.m4v"),
                new { name = "Reel 1.mp4", source = "derivative", length = "867.91", size = "1", original = "Reel 1.m4v" },
            },
        });
        var fetched = new List<string>();
        var originals = new ArchiveOriginals((u, _) => { fetched.Add(u); return Task.FromResult(withSpace); }, NullLogger.Instance, TimeProvider.System);

        var url = await originals.FindAsync($"https://archive.org/download/{Item}/Reel%201.mp4", CancellationToken.None);

        Assert.Equal($"https://archive.org/download/{Item}/Reel%201.m4v", url);
        Assert.Equal([$"https://archive.org/metadata/{Item}"], fetched);
    }

    [Theory]
    [InlineData("https://example.com/download/x/x.mp4")]
    [InlineData("https://archive.org/details/x")]
    [InlineData("http://archive.org/download/x/x.mp4")]
    public async Task Urls_that_are_not_archive_org_downloads_are_left_alone(string url)
    {
        var fetches = 0;
        var originals = new ArchiveOriginals((_, _) => { fetches++; return Task.FromResult("{}"); }, NullLogger.Instance, TimeProvider.System);

        Assert.Null(await originals.FindAsync(url, CancellationToken.None));
        Assert.Equal(0, fetches);
    }

    [Fact]
    public async Task A_failed_lookup_falls_back_and_is_not_retried_straight_away()
    {
        var fetches = 0;
        var originals = new ArchiveOriginals((_, _) => { fetches++; throw new HttpRequestException("503"); }, NullLogger.Instance, TimeProvider.System);

        Assert.Null(await originals.FindAsync(Url, CancellationToken.None));
        Assert.Null(await originals.FindAsync(Url, CancellationToken.None));
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task A_slow_lookup_gives_up_quickly_so_tune_in_is_not_held_up()
    {
        var originals = new ArchiveOriginals(
            async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return "{}"; },
            NullLogger.Instance,
            TimeProvider.System,
            lookupTimeout: TimeSpan.FromMilliseconds(50));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(await originals.FindAsync(Url, CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}
