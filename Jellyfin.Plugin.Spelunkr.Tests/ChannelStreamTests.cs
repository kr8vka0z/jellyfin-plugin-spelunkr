// ChannelStreamTests: one open channel's life, with a shell script standing in for ffmpeg. Opening must return once
// the player has bytes, fail (not hang) when nothing can be played, and closing must leave no files behind.
using System.Reflection;
using Jellyfin.Plugin.Spelunkr.Stream;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Spelunkr.Tests.TestKit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class ChannelStreamTests
{
    [Fact]
    public async Task Open_returns_with_bytes_to_read_and_close_removes_the_streams_files()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "needs /bin/sh");
        using var dir = new TempDir();
        var stream = NewStream(dir.Path, "echo out_time_us=4000000 >&2; printf abc");

        await stream.Open(TestContext.Current.CancellationToken);

        Assert.StartsWith("http://jellyfin.test/LiveTv/LiveStreamFiles/", stream.MediaSource.Path, StringComparison.Ordinal);
        using (var read = stream.GetStream())
        {
            var first = new byte[3];
            await read.ReadExactlyAsync(first, TestContext.Current.CancellationToken);
            Assert.Equal("abc"u8.ToArray(), first);
        }

        await stream.Close();
        stream.Dispose();
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(dir.Path, "streams")));
    }

    [Fact]
    public async Task Open_fails_when_nothing_at_all_can_be_played()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "needs /bin/sh");
        using var dir = new TempDir();
        var stream = NewStream(dir.Path, "exit 1");

        await Assert.ThrowsAsync<IOException>(() => stream.Open(TestContext.Current.CancellationToken));

        // What the tuner does next.
        await stream.Close();
        stream.Dispose();
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(dir.Path, "streams")));
    }

    private static ChannelStream NewStream(string dir, string ffmpegBody)
    {
        var streams = Directory.CreateDirectory(Path.Combine(dir, "streams")).FullName;
        var source = new SpelunkrSource((path, _) => Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path))), NullLogger<SpelunkrSource>.Instance);
        // archive.org is never reached: no originals, no clips.
        var originals = new ArchiveOriginals((_, _) => throw new HttpRequestException("offline"), NullLogger.Instance, TimeProvider.System);
        var clips = new BumpClips((_, _, _) => throw new HttpRequestException("offline"), (_, _) => Task.FromResult<double?>(null), Path.Combine(dir, "clips"), NullLogger.Instance, TimeProvider.System);
        return new ChannelStream("gumshoe", new ChannelLook("GUMSHOE", "#E8DFC8"), Picture.Cpu, SpelunkrTunerHost.NewMediaSource("spelunkr_gumshoe", Picture.Cpu), "tuner", source, originals, clips, Script(dir, "ffmpeg", ffmpegBody), streams, DispatchProxy.Create<IServerApplicationHost, Host>(), NullLogger.Instance);
    }

    // Jellyfin's server host; the stream only asks it for its local address.
    public class Host : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IServerApplicationHost.GetApiUrlForLocalAccess) ? "http://jellyfin.test" : throw new NotSupportedException(targetMethod?.Name);
    }
}
