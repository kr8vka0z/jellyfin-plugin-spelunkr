// BumpClips: downloads bump clips from archive.org ahead of their break and measures them with ffprobe, so a bump job
// only ever reads a finished local file. A clip that isn't ready when its break starts is skipped, never waited for.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>A downloaded clip and its length.</summary>
/// <param name="Path">The local file.</param>
/// <param name="LengthS">Its length in seconds, from ffprobe.</param>
public sealed record ReadyClip(string Path, double LengthS);

/// <summary>Keeps bump clips on disk.</summary>
public sealed class BumpClips
{
    private const string Download = "https://archive.org/download/";

    // Clips are short (the site plays them under one bump); anything bigger isn't one and isn't worth the wait.
    private const long MaxBytes = 64L * 1024 * 1024;

    // A failure (archive.org returns 5xx now and then) is retried at a later break, not at every one.
    private static readonly TimeSpan FailedTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<string, System.IO.Stream, CancellationToken, Task> _download;
    private readonly Func<string, CancellationToken, Task<double?>> _probe;
    private readonly string _dir;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    // ponytail: entries are never evicted; the set is bounded by the clips the channels publish.
    private readonly ConcurrentDictionary<string, ReadyClip> _ready = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _fetching = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="BumpClips"/> class.</summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
    /// <param name="probePath">Jellyfin's ffprobe.</param>
    /// <param name="dir">Where clips are kept.</param>
    /// <param name="logger">Logger.</param>
    public BumpClips(IHttpClientFactory httpClientFactory, string probePath, string dir, ILogger logger)
        : this((url, to, ct) => Fetch(httpClientFactory, url, to, ct), (path, ct) => ProbeAsync(probePath, path, ct), dir, logger, TimeProvider.System)
    {
    }

    internal BumpClips(Func<string, System.IO.Stream, CancellationToken, Task> download, Func<string, CancellationToken, Task<double?>> probe, string dir, ILogger logger, TimeProvider time)
    {
        _download = download;
        _probe = probe;
        _dir = dir;
        _logger = logger;
        _time = time;
    }

    /// <summary>The clip if it is downloaded and measured; null otherwise. Never waits.</summary>
    /// <param name="src">The clip's archive.org URL.</param>
    public ReadyClip? Ready(string src) => _ready.TryGetValue(src, out var clip) && File.Exists(clip.Path) ? clip : null;

    /// <summary>Starts downloading the clip in the background, unless it is ready, under way, or failed recently.</summary>
    /// <param name="src">The clip's archive.org URL.</param>
    /// <returns>The download, for tests; callers don't wait for it.</returns>
    public Task Prefetch(string src)
    {
        if (!src.StartsWith(Download, StringComparison.Ordinal) || Ready(src) is not null
            || (_failed.TryGetValue(src, out var until) && until > _time.GetUtcNow()))
        {
            return Task.CompletedTask;
        }

        lock (_fetching)
        {
            if (!_fetching.TryGetValue(src, out var under) || under.IsCompleted)
            {
                _fetching[src] = under = Task.Run(() => FetchAsync(src));
            }

            return under;
        }
    }

    private async Task FetchAsync(string src)
    {
        var path = Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(src))).ToLowerInvariant() + ".mp4");
        var part = path + ".part";
        try
        {
            using var timeout = new CancellationTokenSource(FetchTimeout, _time);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(_dir);
                var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (file.ConfigureAwait(false))
                {
                    await _download(src, file, timeout.Token).ConfigureAwait(false);
                }

                // Renamed only once complete, so a half-written file is never played.
                File.Move(part, path, true);
            }

            var length = await _probe(path, timeout.Token).ConfigureAwait(false);
            if (length is not > 0)
            {
                File.Delete(path);
                throw new InvalidDataException("ffprobe found no length");
            }

            _ready[src] = new ReadyClip(path, length.Value);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Spelunkr: bump clip {Url} could not be fetched; its break plays without it", src);
            _failed[src] = _time.GetUtcNow() + FailedTtl;
            TryDelete(part);
        }
    }

    private static async Task Fetch(IHttpClientFactory factory, string url, System.IO.Stream to, CancellationToken ct)
    {
        using var response = await factory.CreateClient(NamedClient.Default).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new InvalidDataException($"clip is {response.Content.Headers.ContentLength} bytes");
        }

        var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > MaxBytes)
                {
                    throw new InvalidDataException("clip is too big");
                }

                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<double?> ProbeAsync(string probePath, string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(probePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new IOException("could not start " + probePath);
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .part is overwritten by the next try.
        }
    }
}
