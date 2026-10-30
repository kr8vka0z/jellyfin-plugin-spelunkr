// ChannelStream: one open Spelunkr channel. Runs ffmpeg segment after segment (film, bump, ad, bump, film...)
// and appends the MPEG-TS output to a file that Jellyfin serves at /LiveTv/LiveStreamFiles/<id>/stream.ts
// (through GetStream), the same way Jellyfin's own tuners hand a live stream to its player.
// The file lives in the plugin's own cache folder, not Jellyfin's transcode folder: Jellyfin empties the transcode
// folder when its transcoding service first starts, which deleted a stream opened just after a restart.
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
    // A job whose output stops for this long is killed and replaced by the next fallback.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    // The same, for a job that hasn't written anything yet.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly string _channelId;
    private readonly ChannelLook _look;
    private readonly Picture _picture;
    private readonly SpelunkrSource _source;
    private readonly ArchiveOriginals _originals;
    private readonly string _ffmpegPath;
    private readonly string _workDir;
    private readonly string _filePath;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;
    private DateTime _openedUtc;

    public ChannelStream(string channelId, ChannelLook look, Picture picture, MediaSourceInfo mediaSource, string tunerHostId, SpelunkrSource source, ArchiveOriginals originals, string ffmpegPath, string streamsDir, IServerApplicationHost appHost, ILogger logger)
    {
        _channelId = channelId;
        _look = look;
        _picture = picture;
        _source = source;
        _originals = originals;
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
                await StreamPump.RunAsync(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    (atMs, c) => _source.SegmentAtAsync(_channelId, atMs, c),
                    async (segment, offsetMs, durationMs, outputS, c) => await SegmentPlayer.PlayAsync(
                        segment,
                        _look,
                        offsetMs,
                        durationMs,
                        outputS,
                        (job, c2) => FfmpegProcess.RunAsync(_ffmpegPath, job, _workDir, output, () => started.TrySetResult(), ConnectTimeout, StallTimeout, _logger, c2),
                        _logger,
                        c,
                        segment.Url is null ? null : await _originals.FindAsync(segment.Url, c).ConfigureAwait(false),
                        _picture).ConfigureAwait(false),
                    Task.Delay,
                    ct).ConfigureAwait(false);
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
