// RealFfmpegTests: film jobs run through a real ffmpeg, the way a stream runs them. Every other stream test checks
// argument text, which can't show what ffmpeg does with it. Skipped when no ffmpeg is installed.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Jellyfin.Plugin.Spelunkr.Stream;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Spelunkr.Tests.TestKit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class RealFfmpegTests
{
    private static readonly ChannelLook Look = new("GUMSHOE", "#E8DFC8");

    [Theory]
    [InlineData(0, false)] // the film
    [InlineData(1, false)] // the film with silence, whose silence never ends on its own
    [InlineData(0, true)] // the film in the cabinet, which is looped without end
    public async Task A_film_whose_source_runs_out_early_reports_short_instead_of_the_full_length(int level, bool cabinet)
    {
        Assert.SkipWhen(Ffmpeg is null, "ffmpeg is not installed");
        using var dir = new TempDir();
        // A 3 s film, served over HTTP like archive.org: the job's network options only apply to a URL.
        var film = Path.Combine(dir.Path, "short.mp4");
        Assert.Equal(0, await Run("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=3", "-f", "lavfi", "-i", "sine=d=3", "-c:v", "libx264", "-c:a", "aac", "-movflags", "+faststart", film));
        var picture = Picture.Cpu;
        if (cabinet)
        {
            picture = picture with { Cabinet = Path.Combine(dir.Path, "cabinet.png") };
            Assert.Equal(0, await Run("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=brown@0.5:s=1280x720,format=rgba", "-frames:v", "1", picture.Cabinet));
        }

        using var server = new LoopbackFile(await File.ReadAllBytesAsync(film, TestContext.Current.CancellationToken));
        var job = FfmpegJobs.For(new Segment { Kind = "show", Url = server.Url, Title = "X" }, Look, 0, 30_000, 0, picture: picture)[level];
        using var output = new MemoryStream();

        var emitted = await FfmpegProcess.RunAsync(Ffmpeg!, job, dir.Path, output, () => { }, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), NullLogger.Instance, CancellationToken.None);

        // All of the film and at most the short pad after it, so the player can carry on from there.
        Assert.InRange(emitted, 2_500, 3_000 + (FfmpegJobs.ShortfallPadS * 1000) + 1_000);
        Assert.True(output.Length > 0);
    }

    private static async Task<int> Run(params string[] args) => (await FfmpegProcess.RunToExitAsync(Ffmpeg!, args, TestContext.Current.CancellationToken)).ExitCode;

    /// <summary>Serves one file over HTTP on the loopback interface, to every request, until disposed.</summary>
    private sealed class LoopbackFile : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public LoopbackFile(byte[] body)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                // Ends when Dispose stops the listener.
                while (await _listener.AcceptTcpClientAsync() is var client)
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var request = new StringBuilder();
                            var buffer = new byte[4096];
                            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer) is var n and > 0)
                            {
                                request.Append(Encoding.ASCII.GetString(buffer, 0, n));
                            }

                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                            await stream.WriteAsync(body);
                        }
                        catch (IOException)
                        {
                            // ffmpeg hung up early; the next request gets a fresh connection.
                        }
                    }
                }
            });
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/short.mp4";

        public void Dispose() => _listener.Stop();
    }
}
