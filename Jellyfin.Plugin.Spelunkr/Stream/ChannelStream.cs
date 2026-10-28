// ChannelStream: one open Spelunkr channel. Runs ffmpeg segment after segment (film, bump, ad, bump, film...)
// and appends the MPEG-TS output to a file that Jellyfin serves at /LiveTv/LiveStreamFiles/<id>/stream.ts
// (through GetStream), the same way Jellyfin's own tuners hand a live stream to its player.
// The file lives in the plugin's own cache folder, not Jellyfin's transcode folder: Jellyfin empties the transcode
// folder when its transcoding service first starts, which deleted a stream opened just after a restart.
using System.Diagnostics;
using System.Globalization;
using Jellyfin.Plugin.Spelunkr.Schedule;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>A live Spelunkr channel rendered by ffmpeg.</summary>
internal sealed class ChannelStream : ILiveStream
{
    // Shorter than this left of a segment (we joined at its very end) is skipped, not started.
    private const long MinJobMs = 500;

    private readonly string _channelId;
    private readonly ChannelLook _look;
    private readonly SpelunkrSource _source;
    private readonly string _ffmpegPath;
    private readonly string _workDir;
    private readonly string _filePath;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;
    private DateTime _openedUtc;

    public ChannelStream(string channelId, ChannelLook look, MediaSourceInfo mediaSource, string tunerHostId, SpelunkrSource source, string ffmpegPath, string streamsDir, IServerApplicationHost appHost, ILogger logger)
    {
        _channelId = channelId;
        _look = look;
        _source = source;
        _ffmpegPath = ffmpegPath;
        _appHost = appHost;
        _logger = logger;
        MediaSource = mediaSource;
        TunerHostId = tunerHostId;
        UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _filePath = Path.Combine(streamsDir, UniqueId + ".ts");
        _workDir = Path.Combine(streamsDir, UniqueId);
    }

    public int ConsumerCount { get; set; } = 1;

    public string OriginalStreamId { get; set; } = string.Empty;

    public string TunerHostId { get; }

    public bool EnableStreamSharing { get; set; } = true;

    public MediaSourceInfo MediaSource { get; set; }

    public string UniqueId { get; }

    public async Task Open(CancellationToken openCancellationToken)
    {
        Directory.CreateDirectory(_workDir);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pump = Task.Run(() => PumpAsync(started, _cts.Token), CancellationToken.None);

        MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
        MediaSource.Protocol = MediaProtocol.Http;

        // Return once ffmpeg has written something, so Jellyfin's player has bytes to read straight away.
        await started.Task.WaitAsync(openCancellationToken).ConfigureAwait(false);
        _openedUtc = DateTime.UtcNow;
    }

    public async Task Close()
    {
        EnableStreamSharing = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            await _pump.ConfigureAwait(false);
        }

        TryDelete(() => File.Delete(_filePath));
        TryDelete(() => Directory.Delete(_workDir, true));
    }

    public System.IO.Stream GetStream()
    {
        var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
        // A second viewer joining a running stream starts near the live end, as Jellyfin's own LiveStream does.
        if ((DateTime.UtcNow - _openedUtc).TotalSeconds > 10 && stream.Length > 20000)
        {
            stream.Seek(-20000, SeekOrigin.End);
        }

        return stream;
    }

    public void Dispose() => _cts.Dispose();

    private async Task PumpAsync(TaskCompletionSource started, CancellationToken ct)
    {
        try
        {
            var output = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
            await using (output.ConfigureAwait(false))
            {
                // contentMs: the schedule time the stream has reached. outputS: the stream's own clock, which only
                // counts what was written, so timestamps keep rising even when we skip ahead.
                var contentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var outputS = 0.0;
                while (!ct.IsCancellationRequested)
                {
                    (contentMs, var waitMs) = Pacing.Next(contentMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (waitMs > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
                    }

                    var segment = await _source.SegmentAtAsync(_channelId, contentMs, ct).ConfigureAwait(false);
                    var durationMs = segment.EndMs - contentMs;
                    if (durationMs < MinJobMs)
                    {
                        contentMs = segment.EndMs;
                        continue;
                    }

                    var wrote = false;
                    string? lastError = null;
                    foreach (var job in FfmpegJobs.For(segment, _look, contentMs - segment.StartMs, durationMs, outputS))
                    {
                        (wrote, lastError) = await RunAsync(job, output, started, ct).ConfigureAwait(false);
                        if (wrote)
                        {
                            break;
                        }

                        _logger.LogWarning("Spelunkr {Channel}: ffmpeg gave no output for {Kind} {Id}, trying the next fallback: {Error}", _channelId, segment.Kind, segment.Id, lastError);
                    }

                    if (!wrote && !started.Task.IsCompleted)
                    {
                        throw new IOException($"ffmpeg produced no output for {segment.Kind} {segment.Id}: {lastError}");
                    }

                    contentMs += durationMs;
                    outputS += durationMs / 1000.0;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            started.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spelunkr {Channel}: stream stopped", _channelId);
            started.TrySetException(ex);
        }
        finally
        {
            EnableStreamSharing = false;
        }
    }

    /// <summary>Runs one ffmpeg job, appending its output. Returns whether it wrote anything, and its error text.</summary>
    private async Task<(bool Wrote, string? Error)> RunAsync(FfmpegJob job, FileStream output, TaskCompletionSource started, CancellationToken ct)
    {
        foreach (var (name, text) in job.TextFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(_workDir, name), text, ct).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            WorkingDirectory = _workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in job.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new IOException("could not start ffmpeg at " + _ffmpegPath);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var total = 0L;
        try
        {
            var buffer = new byte[81920];
            var stdout = process.StandardOutput.BaseStream;
            int n;
            while ((n = await stdout.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                // Flush every chunk: Jellyfin reads the file while it grows.
                await output.FlushAsync(ct).ConfigureAwait(false);
                total += n;
                started.TrySetResult();
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        var error = (await stderr.ConfigureAwait(false)).Trim();
        if (total > 0 && process.ExitCode != 0)
        {
            _logger.LogWarning("Spelunkr {Channel}: ffmpeg ended early (exit {Code}): {Error}", _channelId, process.ExitCode, error);
        }

        return (total > 0, error.Length > 0 ? error : null);
    }

    private void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete a Spelunkr temp file");
        }
    }
}
