// ArchiveOriginals: finds the higher-quality original of an archive.org film when Spelunkr links the item's
// compressed derivative, so the stream starts from a better picture. Any doubt keeps Spelunkr's own file.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>Looks up an archive.org item's original video file through archive.org's metadata API.</summary>
public sealed class ArchiveOriginals
{
    private const string Download = "https://archive.org/download/";

    // Above this the original (often a near-lossless master) won't stream smoothly over an ordinary connection
    // while ffmpeg reads it at playback speed; Spelunkr's derivative is ~1 Mbps.
    private const double MaxBitsPerSecond = 8_000_000;

    // The original must be the same cut: Spelunkr's schedule seeks by time into the derivative, so a different
    // length would put the stream at the wrong moment of the film.
    private const double MaxLengthDifferenceS = 1;

    // Files ffmpeg can seek over HTTP straight to a moment (an index in the file). MPEG-2 and AVI originals have
    // no index, so seeking guesses by byte position and can land off schedule.
    // ponytail: by extension only; an original in another seekable container (mkv) is passed over.
    private static readonly string[] Seekable = [".mp4", ".m4v", ".mov"];

    // Items never change their files in practice; a failure (archive.org returns 5xx now and then) is retried soon.
    private static readonly TimeSpan FoundTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan FailedTtl = TimeSpan.FromMinutes(10);

    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lookupTimeout;

    // ponytail: entries are refreshed, never evicted; the set is bounded by the films the channels play.
    private readonly ConcurrentDictionary<string, (string? Url, DateTimeOffset Expires)> _cache = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="ArchiveOriginals"/> class.</summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public ArchiveOriginals(IHttpClientFactory httpClientFactory, ILogger<ArchiveOriginals> logger)
        : this((url, ct) => httpClientFactory.CreateClient(NamedClient.Default).GetStringAsync(url, ct), logger, TimeProvider.System)
    {
    }

    // The lookup runs before a film starts, so it gets only a short wait: a slow archive.org must not delay tune-in.
    internal ArchiveOriginals(Func<string, CancellationToken, Task<string>> fetch, ILogger logger, TimeProvider time, TimeSpan? lookupTimeout = null)
    {
        _fetch = fetch;
        _logger = logger;
        _time = time;
        _lookupTimeout = lookupTimeout ?? TimeSpan.FromSeconds(2.5);
    }

    /// <summary>
    /// The original's download URL for an archive.org derivative URL, or null to keep <paramref name="url"/>
    /// (not an archive.org download, no suitable original, or the lookup failed or was slow).
    /// </summary>
    public async Task<string?> FindAsync(string url, CancellationToken cancellationToken)
    {
        if (!TryParse(url, out var item, out var name))
        {
            return null;
        }

        if (_cache.TryGetValue(url, out var hit) && hit.Expires > _time.GetUtcNow())
        {
            return hit.Url;
        }

        string? found = null;
        var ttl = FoundTtl;
        using var timeout = new CancellationTokenSource(_lookupTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var metadata = await _fetch("https://archive.org/metadata/" + Uri.EscapeDataString(item), linked.Token).ConfigureAwait(false);
            if (Pick(metadata, name) is { } original)
            {
                found = Download + Uri.EscapeDataString(item) + "/" + string.Join('/', original.Split('/').Select(Uri.EscapeDataString));
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "No archive.org original for {Url}; playing Spelunkr's file", url);
            ttl = FailedTtl;
        }

        _cache[url] = (found, _time.GetUtcNow() + ttl);
        return found;
    }

    /// <summary>
    /// The name of the original to play instead of <paramref name="derivativeName"/>, or null. The derivative's own
    /// "original" field names it; it must be a seekable video of the same length and a streamable bitrate.
    /// </summary>
    internal static string? Pick(string metadataJson, string derivativeName)
    {
        using var doc = JsonDocument.Parse(metadataJson);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? Find(string name) => files.EnumerateArray()
            .Where(f => f.ValueKind == JsonValueKind.Object && Text(f, "name") == name)
            .Select(f => (JsonElement?)f).FirstOrDefault();

        if (Find(derivativeName) is not { } derivative
            || Text(derivative, "source") != "derivative"
            || Text(derivative, "original") is not { Length: > 0 } originalName
            || !Seekable.Any(ext => originalName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            || Find(originalName) is not { } original
            || Seconds(derivative) is not { } derivativeS
            || Seconds(original) is not { } originalS
            || originalS <= 0
            || Math.Abs(originalS - derivativeS) > MaxLengthDifferenceS
            || Number(Text(original, "size")) is not { } bytes
            || bytes * 8 / originalS > MaxBitsPerSecond)
        {
            return null;
        }

        return originalName;
    }

    /// <summary>Splits "https://archive.org/download/&lt;item&gt;/&lt;file&gt;" into the item id and the (unescaped) file name.</summary>
    private static bool TryParse(string url, out string item, out string name)
    {
        item = name = string.Empty;
        if (!url.StartsWith(Download, StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Query.Length > 0)
        {
            return false;
        }

        var parts = uri.AbsolutePath.Split('/', 4); // "", "download", item, file
        if (parts.Length < 4 || parts[2].Length == 0 || parts[3].Length == 0)
        {
            return false;
        }

        item = Uri.UnescapeDataString(parts[2]);
        name = Uri.UnescapeDataString(parts[3]);
        return true;
    }

    // archive.org gives numbers as strings ("867.9") or, on some items, as JSON numbers.
    private static string? Text(JsonElement file, string key) => file.TryGetProperty(key, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    } : null;

    private static double? Number(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    // Length is seconds ("867.9") on most items and "h:m:s" ("00:14:27.9") on some older ones.
    private static double? Seconds(JsonElement file)
    {
        var total = 0.0;
        foreach (var part in (Text(file, "length") ?? string.Empty).Split(':'))
        {
            if (Number(part) is not { } n)
            {
                return null;
            }

            total = (total * 60) + n;
        }

        return total;
    }
}
