// FfmpegProcess: runs one ffmpeg job, copies its MPEG-TS output into the stream file, and reports how much
// media it actually produced (from ffmpeg's -progress lines). Kills a job whose output stalls.
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>Runs ffmpeg jobs.</summary>
internal static class FfmpegProcess
{
    /// <summary>
    /// Runs <paramref name="job"/> in <paramref name="workDir"/>, appending stdout to <paramref name="output"/>.
    /// Returns the ms of media emitted. A job that writes nothing for <paramref name="stallTimeout"/> is killed:
    /// ffmpeg's own network timeout covers most stalls, this catches the rest.
    /// </summary>
    public static async Task<long> RunAsync(string exe, FfmpegJob job, string workDir, System.IO.Stream output, Action onBytes, TimeSpan stallTimeout, ILogger logger, CancellationToken ct)
    {
        foreach (var (name, text) in job.TextFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, name), text, ct).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in job.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new IOException("could not start " + exe);
        long emittedUs = 0;
        var errors = new List<string>();
        var stderr = Task.Run(
            async () =>
            {
                while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                    {
                        if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                        {
                            Interlocked.Exchange(ref emittedUs, Math.Max(us, Interlocked.Read(ref emittedUs)));
                        }
                    }
                    else if (!line.Contains('=', StringComparison.Ordinal))
                    {
                        errors.Add(line);
                    }
                }
            },
            CancellationToken.None);

        try
        {
            var buffer = new byte[81920];
            var stdout = process.StandardOutput.BaseStream;
            while (true)
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
                read.CancelAfter(stallTimeout);
                int n;
                try
                {
                    n = await stdout.ReadAsync(buffer, read.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Spelunkr: ffmpeg sent nothing for {Seconds} s; stopping it", stallTimeout.TotalSeconds);
                    break;
                }

                if (n == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                // Flush every chunk: Jellyfin reads the file while it grows.
                await output.FlushAsync(ct).ConfigureAwait(false);
                onBytes();
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
        }

        if (process.ExitCode != 0)
        {
            logger.LogWarning("Spelunkr: ffmpeg exited {Code}: {Error}", process.ExitCode, string.Join(" | ", errors));
        }

        return Interlocked.Read(ref emittedUs) / 1000;
    }
}
