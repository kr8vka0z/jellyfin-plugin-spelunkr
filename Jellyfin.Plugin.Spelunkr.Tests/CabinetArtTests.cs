// CabinetArtTests: the channel must always open. Each way of not getting a logo onto the plate falls back to the
// empty cabinet, and the art itself ships for both stream sizes with a see-through tube.
using Jellyfin.Plugin.Spelunkr.Stream;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public sealed class CabinetArtTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spelunkr-cabinet-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static LineupChannel Channel(bool logo) =>
        new("x", "X", "#336699", 1, [], Logomark: logo ? new Logomark("pixel", ["c.", ".c"]) : null);

    private Task<string?> Cabinet(string ffmpeg, bool logo, int height = 1080) =>
        CabinetArt.ForChannelAsync(ffmpeg, _dir, height, Channel(logo), NullLogger.Instance, CancellationToken.None);

    [Theory]
    [InlineData(1080)]
    [InlineData(720)]
    public async Task A_channel_with_no_logo_gets_the_empty_cabinet_without_running_ffmpeg(int height)
    {
        var path = await Cabinet("/nonexistent/ffmpeg", false, height);

        Assert.True(new FileInfo(path!).Length > 10_000);
    }

    [Theory]
    [InlineData("/nonexistent/ffmpeg")] // can't start
    [InlineData("/bin/false")] // starts and fails
    public async Task A_logo_that_cannot_be_added_leaves_the_empty_cabinet(string ffmpeg)
    {
        var plain = await Cabinet(ffmpeg, false);

        Assert.Equal(plain, await Cabinet(ffmpeg, true));
        Assert.Single(Directory.GetFiles(_dir)); // nothing half-made left behind
    }

    [Fact]
    public async Task No_cabinet_at_all_if_it_cannot_be_written()
    {
        File.WriteAllText(_dir, "a file where the folder should be");
        try
        {
            Assert.Null(await Cabinet("/nonexistent/ffmpeg", false));
        }
        finally
        {
            File.Delete(_dir);
        }
    }
}
