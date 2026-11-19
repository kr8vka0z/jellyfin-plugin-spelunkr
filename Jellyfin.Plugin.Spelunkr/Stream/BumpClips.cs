// BumpClips: fetches the stretch of an archive.org clip that a break uses, ahead of the break, and measures it with
// ffprobe, so a bump job only ever reads a finished local file. A clip that isn't ready when its break starts is
// skipped, never waited for.
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>A fetched clip stretch and where it sits in the clip.</summary>
/// <param name="Path">The local file.</param>
/// <param name="LengthS">The whole clip's length as the clip clock sees it (see <see cref="BumpClipVideo.ClipTimeS"/>).</param>
/// <param name="OffsetS">Where in the whole clip the file starts, seconds; 0 for a whole file.</param>
public sealed record ReadyClip(string Path, double LengthS, double OffsetS = 0);

/// <summary>Part of a remote clip to fetch.</summary>
/// <param name="Src">The clip's archive.org URL.</param>
/// <param name="StartS">Where the stretch starts in the clip.</param>
/// <param name="LengthS">How long it is; null to the clip's end.</param>
internal sealed record ClipStretch(string Src, double StartS, double? LengthS);

/// <summary>Keeps bump clips on disk.</summary>
public sealed class BumpClips
{
    // Live clips are whole archive.org episodes (160-200 MB) of which a break shows the 15-20 s before the cut-off
    // point, so only that stretch is fetched: from LeadS before the first frame shown to TailS past the cut-off.
    // ffmpeg reads the URL with range requests, so only about the stretch's bytes come down.
    private const double LeadS = 2;
    private const double TailS = 1;

    // A break's clip plays over a few slides; a longer stretch is a plan this doesn't expect and isn't worth the fetch.
    private const double MaxStretchS = 60;

    // A failure is retried after ArchiveOriginals.FailedTtl, at a later break, not at every one.
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<ClipStretch, string, CancellationToken, Task> _cut;
    private readonly Func<string, CancellationToken, Task<double?>> _probe;
    private readonly string _dir;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    // ponytail: entries are never evicted; the set is bounded by the clips the channels publish.
    private readonly ConcurrentDictionary<string, ReadyClip> _ready = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _fetching = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="BumpClips"/> class.</summary>
    /// <param name="ffmpegPath">Jellyfin's ffmpeg, asked at each fetch: Jellyfin may set it after this is built.</param>
    /// <param name="probePath">Jellyfin's ffprobe, likewise.</param>
    /// <param name="dir">Where clips are kept.</param>
    /// <param name="logger">Logger.</param>
    public BumpClips(Func<string> ffmpegPath, Func<string> probePath, string dir, ILogger logger)
        : this((stretch, to, ct) => CutAsync(ffmpegPath(), stretch, to, ct), (path, ct) => ProbeAsync(probePath(), path, ct), dir, logger, TimeProvider.System)
    {
    }

    /// <param name="cut">Writes a stretch of a remote clip to a local file.</param>
    /// <param name="probe">A file's or URL's length in seconds, or null.</param>
    internal BumpClips(Func<ClipStretch, string, CancellationToken, Task> cut, Func<string, CancellationToken, Task<double?>> probe, string dir, ILogger logger, TimeProvider time)
    {
        _cut = cut;
        _probe = probe;
        _dir = dir;
        _logger = logger;
        _time = time;
    }

    /// <summary>The clip's stretch if it is fetched and measured; null otherwise. Never waits.</summary>
    /// <param name="plan">The clip's plan.</param>
    public ReadyClip? Ready(BumpClip plan) => _ready.TryGetValue(Key(plan), out var clip) && File.Exists(clip.Path) ? clip : null;

    /// <summary>Starts fetching the clip's stretch in the background, unless it is ready, under way, or failed recently.</summary>
    /// <param name="plan">The clip's plan.</param>
    /// <returns>The fetch, for tests; callers don't wait for it.</returns>
    public Task Prefetch(BumpClip plan)
    {
        var key = Key(plan);
        if (!plan.Src.StartsWith(ArchiveOriginals.Download, StringComparison.Ordinal) || Ready(plan) is not null
            || (_failed.TryGetValue(key, out var until) && until > _time.GetUtcNow()))
        {
            return Task.CompletedTask;
        }

        // Known from the plan alone: from LeadS before the first frame shown to the cut-off (plus TailS) or the end.
        var stretchS = ((plan.MotionEndMs - plan.StartMs) / 1000.0) + LeadS + (plan.To is > 0 ? TailS : 0);
        if (stretchS > MaxStretchS)
        {
            _logger.LogInformation("Spelunkr: bump clip {Url} would need {Seconds:0} s of the episode; its break plays without it", plan.Src, stretchS);
            _failed[key] = _time.GetUtcNow() + ArchiveOriginals.FailedTtl;
            return Task.CompletedTask;
        }

        lock (_fetching)
        {
            if (!_fetching.TryGetValue(key, out var under) || under.IsCompleted)
            {
                _fetching[key] = under = Task.Run(() => FetchAsync(plan, key));
            }

            return under;
        }
    }

    // The stretch depends only on the clip, its cut-off and how long it moves, so two breaks cutting the same
    // episode differently get their own files.
    private static string Key(BumpClip plan) => string.Create(CultureInfo.InvariantCulture, $"{plan.Src}\n{plan.To:R}\n{plan.MotionEndMs - plan.StartMs}");

    private async Task FetchAsync(BumpClip plan, string key)
    {
        var path = Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".mp4");
        try
        {
            using var timeout = new CancellationTokenSource(FetchTimeout, _time);
            // The clock ends a clip at its cut-off point, or at its end without one, which only ffprobe knows. That
            // length is kept as measured: the whole file's own length sets where it holds.
            var wholeS = plan.To is > 0 ? null : await _probe(plan.Src, timeout.Token).ConfigureAwait(false);
            if (plan.To is not > 0 && wholeS is not > 0)
            {
                throw new InvalidDataException("ffprobe found no length");
            }

            var playS = plan.To is > 0 ? plan.To.Value : wholeS!.Value;
            // On a frame of the stream's own 30 fps, counted from the episode's start, so the stretch's frames are the
            // ones the whole file would give (BumpClipVideo turns every clip into 30 fps from its first frame).
            var startS = Math.Floor(Math.Max(0, playS - ((plan.MotionEndMs - plan.StartMs) / 1000.0) - LeadS) * Picture.Fps) / Picture.Fps;
            Directory.CreateDirectory(_dir);
            await StoredFile.SaveAsync(path, part => _cut(new ClipStretch(plan.Src, startS, plan.To is > 0 ? playS + TailS - startS : null), part, timeout.Token)).ConfigureAwait(false);

            var length = await _probe(path, timeout.Token).ConfigureAwait(false);
            // An episode that ends before its cut-off point plays to its end on the site, from a part not fetched.
            if (length is not > 0 || (plan.To is > 0 && startS + length <= plan.To))
            {
                File.Delete(path);
                throw new InvalidDataException(length is > 0 ? "the clip ends before its cut-off point" : "ffprobe found no length");
            }

            // With a cut-off any length past it gives the same clock; the stretch reaches past it.
            _ready[key] = new ReadyClip(path, wholeS ?? (startS + length.Value), startS);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Spelunkr: bump clip {Url} could not be fetched; its break plays without it", plan.Src);
            _failed[key] = _time.GetUtcNow() + ArchiveOriginals.FailedTtl;
        }
    }

    /// <summary>
    /// Writes <paramref name="stretch"/> of a remote clip to <paramref name="to"/> as a muted H.264 mp4 that starts
    /// exactly at its start: the -ss before the input seeks by the file's index, so only about the stretch is read,
    /// and the re-encode drops the frames before the start that a copy would have to keep from the keyframe.
    /// </summary>
    /// <remarks>
    /// The first frame after a seek usually lands a little after the asked start (the next source frame), and the
    /// bump job counts from a file's first frame. fps with start_time=0 fills that gap with the first frame, so the
    /// file's time 0 is the asked start.
    /// </remarks>
    internal static async Task CutAsync(string ffmpegPath, ClipStretch stretch, string to, CancellationToken ct)
    {
        string[] length = stretch.LengthS is { } s ? ["-t", Seconds(s)] : [];
        // The card is at most 1920x1080 (a GPU stream); a bigger source is cut down to that, a smaller one kept.
        var (code, _) = await FfmpegProcess.RunToExitAsync(
            ffmpegPath,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. FfmpegJobs.Remote, "-ss", Seconds(stretch.StartS), .. length, "-i", stretch.Src,
             "-map", "0:v:0", "-vf", $"fps={Picture.Fps}:start_time=0,scale='min(iw,1920)':'min(ih,1080)':force_original_aspect_ratio=decrease:force_divisible_by=2",
             "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-f", "mp4", to],
            ct).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidDataException($"ffmpeg exited with {code}");
        }
    }

    // A probe still running when ct fires (the fetch timeout) is killed. A URL is read with the network options.
    internal static async Task<double?> ProbeAsync(string probePath, string path, CancellationToken ct)
    {
        string[] remote = path.StartsWith("https://", StringComparison.Ordinal) || path.StartsWith("http://", StringComparison.Ordinal) ? FfmpegJobs.Remote : [];
        var (code, output) = await FfmpegProcess.RunToExitAsync(probePath, ["-v", "error", .. remote, "-show_entries", "format=duration", "-of", "csv=p=0", path], ct).ConfigureAwait(false);
        return code == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    private static string Seconds(double s) => s.ToString("0.######", CultureInfo.InvariantCulture);
}
