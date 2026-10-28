// SpelunkrSource: fetches Spelunkr's public lineup, manifests and platform files, keeps the schedule engine
// loaded with every version the lineup lists, and answers schedule questions for the tuner and the guide.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Spelunkr.Schedule;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr;

/// <summary>Spelunkr's data plus the schedule engine, shared by the tuner and the guide.</summary>
public sealed partial class SpelunkrSource
{
    /// <summary>Spelunkr's public storage bucket (no login needed).</summary>
    public const string Bucket = "https://jbuopkuluyfjwgsrigry.supabase.co/storage/v1/object/public/broadcast/";


    // Versions change every few days and a new one is listed ahead of when it takes over, so hourly is plenty.
    private static readonly TimeSpan LineupMaxAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly Func<string, CancellationToken, Task<string>> _read;
    private readonly ILogger<SpelunkrSource> _logger;
    private readonly Action _guideChanged;
    private readonly TimeProvider _time;
    private readonly ScheduleEngine _engine = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Dictionary<string, string> _platforms = new(StringComparer.Ordinal);
    private readonly HashSet<(string Channel, string Manifest)> _loadedManifests = [];
    // _platforms and _loadedManifests are only touched under _refresh. _details is also read by the guide while a
    // refresh may be writing it, hence the concurrent dictionary.
    private readonly ConcurrentDictionary<(string Channel, int Version), IReadOnlyList<ItemDetails>> _details = new();
    private Lineup? _lineup;
    private DateTimeOffset _nextRefreshUtc;

    /// <summary>Initializes a new instance of the <see cref="SpelunkrSource"/> class.</summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
    /// <param name="taskManager">Jellyfin's task manager, to refresh the guide when a new schedule version appears.</param>
    /// <param name="logger">Logger.</param>
    public SpelunkrSource(IHttpClientFactory httpClientFactory, ITaskManager taskManager, ILogger<SpelunkrSource> logger)
        : this(
            (path, ct) => httpClientFactory.CreateClient(NamedClient.Default).GetStringAsync(Bucket + path, ct),
            logger,
            () => _ = Registration.QueueGuideRefreshAsync(taskManager, logger),
            TimeProvider.System)
    {
    }

    internal SpelunkrSource(Func<string, CancellationToken, Task<string>> read, ILogger<SpelunkrSource> logger, Action? guideChanged = null, TimeProvider? time = null)
    {
        _read = read;
        _logger = logger;
        _guideChanged = guideChanged ?? (() => { });
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The channels this plugin offers, as the lineup describes them.</summary>
    public async Task<IReadOnlyList<LineupChannel>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var lineup = await GetLineupAsync(cancellationToken).ConfigureAwait(false);
        return lineup.Channels.Where(c => IsOffered(c) && _engine.HasChannel(c.Id)).ToList();
    }

    /// <summary>The segment playing on a channel at a moment (Unix ms).</summary>
    public async Task<Segment> SegmentAtAsync(string channelId, long atMs, CancellationToken cancellationToken)
    {
        var lineup = await GetLineupAsync(cancellationToken).ConfigureAwait(false);
        return _engine.SegmentAt(channelId, atMs, lineup.AudioBase);
    }

    /// <summary>Every segment on a channel between two moments (Unix ms).</summary>
    public async Task<IReadOnlyList<Segment>> SegmentsBetweenAsync(string channelId, long fromMs, long toMs, CancellationToken cancellationToken)
    {
        var lineup = await GetLineupAsync(cancellationToken).ConfigureAwait(false);
        return _engine.SegmentsBetween(channelId, fromMs, toMs, lineup.AudioBase);
    }

    private async Task<Lineup> GetLineupAsync(CancellationToken cancellationToken)
    {
        if (_lineup is not null && _time.GetUtcNow() < _nextRefreshUtc)
        {
            return _lineup;
        }

        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lineup is not null && _time.GetUtcNow() < _nextRefreshUtc)
            {
                return _lineup;
            }

            try
            {
                var lineup = JsonSerializer.Deserialize<Lineup>(await _read("lineup.json", cancellationToken).ConfigureAwait(false), ScheduleEngine.Json)
                    ?? throw new InvalidDataException("lineup.json is empty");
                var added = false;
                foreach (var channel in lineup.Channels.Where(IsOffered))
                {
                    // One channel's bad or missing file must not take the other channels down with it; this one
                    // is left out (or keeps the versions it had) and is retried at the next refresh.
                    try
                    {
                        foreach (var pointer in channel.Versions)
                        {
                            added |= await LoadVersionAsync(channel.Id, pointer, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (!IsCallerCancel(ex, cancellationToken))
                    {
                        _logger.LogWarning(ex, "Could not load Spelunkr channel {Channel}", channel.Id);
                    }
                }

                // The stream follows a new version on its own, but Jellyfin only re-reads the guide when its guide
                // task runs (daily), so ask for a run now. The first load is covered by Registration's refresh.
                if (added && _lineup is not null)
                {
                    _guideChanged();
                }

                _lineup = lineup;
                _nextRefreshUtc = _time.GetUtcNow() + LineupMaxAge;
            }
            catch (Exception ex) when (_lineup is not null && !IsCallerCancel(ex, cancellationToken))
            {
                // Keep playing from what we already have; Spelunkr being briefly unreachable shouldn't stop a channel.
                _logger.LogWarning(ex, "Could not refresh the Spelunkr lineup; using the copy from earlier");
                _nextRefreshUtc = _time.GetUtcNow() + RetryAfterFailure;
            }

            return _lineup!;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>
    /// Whether this plugin offers a lineup channel: it has a channel number and isn't marked coming-soon or debug,
    /// the same channels spelunkr.tv lists.
    /// </summary>
    /// <remarks>
    /// ponytail: audio-overlay channels (vacation-slides) are left out. They are silent films the site scores with
    /// a playlist of archive.org recordings picked per film by the engine and looked up through archive.org's
    /// metadata API; this plugin doesn't build that music track yet, and the films alone would play in silence.
    /// </remarks>
    internal static bool IsOffered(LineupChannel c) => SafeId().IsMatch(c.Id) && c.Dial is not null && !c.ComingSoon && !c.DebugOnly && !c.AudioOverlay;

    // Channel ids end up in Jellyfin ids, URLs and logs; Spelunkr's own tooling only makes ids of this shape.
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex SafeId();

    /// <summary>Spelunkr's write-up of a film (description, director, cast), if its details file had one.</summary>
    public ItemDetails? Details(string channelId, Segment show)
        => show.VideoIndex is int i && _details.TryGetValue((channelId, show.Version), out var list) && i >= 0 && i < list.Count ? list[i] : null;

    /// <summary>
    /// A path taken from Spelunkr's data (manifest, platform file) that is about to be appended to the bucket URL.
    /// Only plain relative paths are accepted: no "..", no query or fragment, no scheme, so data can't point us elsewhere.
    /// </summary>
    internal static string BucketPath(string? path)
    {
        if (path is null || !PlainPath().IsMatch(path) || path.Split('/').Any(part => part is "." or ".."))
        {
            throw new InvalidDataException($"'{path}' is not a plain bucket path");
        }

        return path;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex PlainPath();

    /// <summary>Loads one version into the engine; false if it was already loaded.</summary>
    private async Task<bool> LoadVersionAsync(string channelId, JsonElement pointer, CancellationToken cancellationToken)
    {
        var manifestPath = BucketPath(pointer.GetProperty("manifest").GetString());
        if (_loadedManifests.Contains((channelId, manifestPath)))
        {
            return false;
        }

        var manifest = await _read(manifestPath, cancellationToken).ConfigureAwait(false);
        string? platform = null;
        using (var doc = JsonDocument.Parse(manifest))
        {
            if (doc.RootElement.TryGetProperty("platform", out var p) && p.GetString() is { Length: > 0 } platformRef)
            {
                var platformPath = BucketPath(platformRef);
                if (!_platforms.TryGetValue(platformPath, out platform))
                {
                    platform = await _read(platformPath, cancellationToken).ConfigureAwait(false);
                    _platforms[platformPath] = platform;
                }
            }
        }

        _engine.AddVersion(channelId, manifest, platform, pointer.GetRawText());
        await LoadDetailsAsync(channelId, pointer, cancellationToken).ConfigureAwait(false);
        _loadedManifests.Add((channelId, manifestPath));
        _logger.LogInformation("Loaded Spelunkr schedule {Manifest}", manifestPath);
        return true;
    }

    /// <summary>
    /// True only when the caller asked to stop. HttpClient reports its own timeout as a cancellation too; that one
    /// is an ordinary fetch failure and must be handled like any other.
    /// </summary>
    private static bool IsCallerCancel(Exception ex, CancellationToken cancellationToken)
        => ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>Loads a version's details file (film descriptions for the guide). Optional: a failure only costs descriptions.</summary>
    private async Task LoadDetailsAsync(string channelId, JsonElement pointer, CancellationToken cancellationToken)
    {
        if (!pointer.TryGetProperty("details", out var d) || d.GetString() is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            var details = JsonSerializer.Deserialize<DetailsFile>(await _read(BucketPath(path), cancellationToken).ConfigureAwait(false), ScheduleEngine.Json);
            _details[(channelId, pointer.GetProperty("v").GetInt32())] = details?.Items ?? [];
        }
        catch (Exception ex) when (!IsCallerCancel(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "Could not load Spelunkr film details {Path}; the guide shows titles only", path);
        }
    }

    private sealed record DetailsFile(IReadOnlyList<ItemDetails> Items);
}

/// <summary>Spelunkr's write-up of one film, from a version's details file (same order as the manifest's items).</summary>
public sealed record ItemDetails(string? Description, string? Director, IReadOnlyList<string>? Stars, string? ImdbId);

/// <summary>Spelunkr's lineup.json (only the fields this plugin reads).</summary>
public sealed record Lineup(string AudioBase, int Engine, IReadOnlyList<LineupChannel> Channels);

/// <summary>One channel in the lineup.</summary>
public sealed record LineupChannel(
    string Id,
    string Name,
    string Color,
    int? Dial,
    IReadOnlyList<JsonElement> Versions,
    string? Family = null,
    bool ComingSoon = false,
    bool DebugOnly = false,
    bool AudioOverlay = false,
    Logomark? Logomark = null);

/// <summary>A channel's pixel-art logo: square rows of palette letters, "." for transparent.</summary>
public sealed record Logomark(string Type, IReadOnlyList<string>? Rows);
