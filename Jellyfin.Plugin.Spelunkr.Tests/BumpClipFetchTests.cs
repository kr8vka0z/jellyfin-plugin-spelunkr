// BumpClipFetchTests: a bump clip's stretch fetched through a real ffmpeg from a server that serves parts of a file on
// request, as archive.org does. Proves only a small part of a long episode comes down and that the frames fetched are
// the ones the break shows. Skipped when no ffmpeg is installed.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Spelunkr.Stream;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Spelunkr.Tests.TestKit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class BumpClipFetchTests
{
    // The test episode: 10 minutes at 15 fps, each frame's number drawn as 14 black-or-white bars along its top, over
    // noise so it is as big as a real file's minute of video.
    private const int EpisodeS = 600;
    private const int Fps = 15;
    private const int Width = 224;
    private const int BarW = 16;
    private const int Bits = 14;

    private static readonly string? Ffprobe = Ffmpeg is null ? null : Path.Combine(Path.GetDirectoryName(Ffmpeg)!, "ffprobe");

    // Made once per run (about 15 s): with its index (moov) at the end, as ffmpeg writes it, and moved to the front.
    private static readonly Lazy<Task<(byte[] IndexAtEnd, byte[] FastStart)>> Episodes = new(MakeEpisodesAsync);

    [Theory]
    [InlineData(true, 500.0)] // index first, cut off at clipTo
    [InlineData(false, 500.0)] // index last: ffmpeg reads the end, then the stretch
    [InlineData(true, null)] // no cut-off: plays to the episode's end
    public async Task Only_the_stretch_a_break_uses_comes_down_and_from_the_right_moment(bool fastStart, double? to)
    {
        Assert.SkipWhen(Ffmpeg is null || !File.Exists(Ffprobe), "ffmpeg and ffprobe are not installed");
        var episodes = await Episodes.Value;
        var episode = fastStart ? episodes.FastStart : episodes.IndexAtEnd;
        using var server = new RangeServer(episode);
        using var dir = new TempDir();
        // The plan names archive.org; the fetch is sent to the local server instead, through the same code.
        string Local(string url) => url.StartsWith(ArchiveOriginals.Download, StringComparison.Ordinal) ? server.Url : url;
        var clips = new BumpClips(
            (stretch, file, ct) => BumpClips.CutAsync(Ffmpeg!, stretch with { Src = Local(stretch.Src) }, file, ct),
            (path, ct) => BumpClips.ProbeAsync(Ffprobe!, Local(path), ct),
            dir.Path,
            NullLogger.Instance,
            TimeProvider.System);
        var plan = BumpClipTests.ClipBump("wide").Clip! with { To = to };

        await clips.Prefetch(plan);
        var ready = clips.Ready(plan);

        Assert.NotNull(ready);
        // About 12 s of a 10-minute episode (2%), plus the index and a keyframe's run-up.
        Assert.InRange(server.Sent, 1, episode.Length / 10);
        // 9.06 s of motion slides and the 2 s before them; the clock's length is the episode's when it plays to the end.
        Assert.Equal(Math.Floor(((to ?? EpisodeS) - 9.06 - 2) * 30) / 30, ready.OffsetS, 6);
        Assert.InRange(ready.LengthS, (to ?? EpisodeS) - 0.1, to is null ? EpisodeS + 0.1 : to.Value + 1.1);

        // The plugin's own trim of the file, from the first frame shown to the held frame, must show the same moments
        // of the episode as the whole file would.
        var trim = Regex.Match(BumpClipVideo.Under(plan, ready, 0, 1280, 720, 2, "#000000"), @"setpts=PTS-STARTPTS,fps=30.*?(trim=start=[\d.]+:end=[\d.]+)");
        var (first, last) = await EpisodeTimesAsync(ready.Path, "setpts=PTS-STARTPTS,fps=30," + trim.Groups[1].Value, dir.Path);
        Assert.Equal(BumpClipVideo.ClipTimeS(plan, ready.LengthS, plan.StartMs / 1000.0), first, 1.5 / Fps);
        Assert.Equal(BumpClipVideo.ClipTimeS(plan, ready.LengthS, plan.MotionEndMs / 1000.0), last, 1.5 / Fps);
    }

    // Which moments of the episode the first and last frames out of the filter show: the frame numbers in their bars.
    private static async Task<(double First, double Last)> EpisodeTimesAsync(string file, string filter, string dir)
    {
        var raw = Path.Combine(dir, "frames.gray");
        Assert.Equal(0, await Run("-hide_banner", "-loglevel", "error", "-y", "-i", file, "-vf", filter, "-f", "rawvideo", "-pix_fmt", "gray", raw));
        var frames = await File.ReadAllBytesAsync(raw, TestContext.Current.CancellationToken);
        double At(int start)
        {
            var frame = 0;
            for (var bit = 0; bit < Bits; bit++)
            {
                frame |= frames[start + (36 * Width) + (bit * BarW) + (BarW / 2)] > 128 ? 1 << bit : 0;
            }

            return frame / (double)Fps;
        }

        return (At(0), At(frames.Length - (Width * 144)));
    }

    private static async Task<(byte[], byte[])> MakeEpisodesAsync()
    {
        var dir = Directory.CreateTempSubdirectory("spelunkr-episode").FullName;
        try
        {
            var (atEnd, fast) = (Path.Combine(dir, "end.mp4"), Path.Combine(dir, "fast.mp4"));
            var bars = string.Create(CultureInfo.InvariantCulture, $"nullsrc=s={Bits}x2:r={Fps}:d={EpisodeS},format=gray,geq=lum='255*mod(floor(N/pow(2\\,X))\\,2)',scale={Width}:72:flags=neighbor,pad={Width}:144:0:0:gray,noise=alls=12:allf=t");
            Assert.Equal(0, await Run("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", bars, "-c:v", "libx264", "-preset", "ultrafast", "-g", "150", "-pix_fmt", "yuv420p", atEnd));
            Assert.Equal(0, await Run("-hide_banner", "-loglevel", "error", "-i", atEnd, "-c", "copy", "-movflags", "+faststart", fast));
            return (await File.ReadAllBytesAsync(atEnd), await File.ReadAllBytesAsync(fast));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static async Task<int> Run(params string[] args) => (await FfmpegProcess.RunToExitAsync(Ffmpeg!, args, CancellationToken.None)).ExitCode;

    /// <summary>Serves one file over HTTP on the loopback interface, a part of it when asked, counting what it sends.</summary>
    private sealed class RangeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private long _sent;

        public RangeServer(byte[] body)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                // Ends when Dispose stops the listener. ffmpeg opens a new connection for each seek.
                while (await _listener.AcceptTcpClientAsync() is var client)
                {
                    _ = Task.Run(() => ServeAsync(client, body));
                }
            });
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/episode.mp4";

        /// <summary>Gets the bytes of the file handed to the network so far.</summary>
        public long Sent => Interlocked.Read(ref _sent);

        public void Dispose() => _listener.Stop();

        private async Task ServeAsync(TcpClient client, byte[] body)
        {
            using (client)
            {
                try
                {
                    // A small buffer, so what counts as sent is close to what ffmpeg read before hanging up.
                    client.SendBufferSize = 16 * 1024;
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer) is var n and > 0)
                    {
                        request.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }

                    var range = Regex.Match(request.ToString(), @"Range: bytes=(\d+)-(\d*)", RegexOptions.IgnoreCase);
                    long start = range.Success ? long.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                    long last = range.Success && range.Groups[2].Length > 0 ? Math.Min(long.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture), body.Length - 1) : body.Length - 1;
                    var head = range.Success
                        ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{last}/{body.Length}\r\n"
                        : "HTTP/1.1 200 OK\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head + $"Content-Type: video/mp4\r\nAccept-Ranges: bytes\r\nContent-Length: {last - start + 1}\r\nConnection: close\r\n\r\n"));
                    for (var at = start; at <= last; at += 16 * 1024)
                    {
                        var count = (int)Math.Min(16 * 1024, last - at + 1);
                        await stream.WriteAsync(body.AsMemory((int)at, count));
                        Interlocked.Add(ref _sent, count);
                    }
                }
                catch (IOException)
                {
                    // ffmpeg hung up: it has what it wanted from this part.
                }
            }
        }
    }
}
