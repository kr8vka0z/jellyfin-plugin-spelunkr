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
    /// <summary>Spelunkr's public data host (no login needed); the site reads the same one.</summary>
    public const string Bucket = "https://data.spelunkr.tv/";

    // Since 2026-11-06 the data host answers 403 to any request that does not name spelunkr.tv as its Referer.
    private static readonly Uri SiteReferer = new("https://spelunkr.tv/");

    // A new version can take over within minutes of being listed (sometimes it is listed after its start), so the
    // refresh timer checks every 10 minutes. An unchanged lineup costs one small download: loaded manifests are skipped.
    private static readonly TimeSpan LineupMaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    // Each data read gets its own limit: a host that accepts and then stalls would otherwise hold the refresh (and,
    // on the first load, every caller) for HttpClient's 100 s per file.
    private static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(15);

    private readonly Func<string, CancellationToken, Task<string>> _read;
    private readonly ILogger<SpelunkrSource> _logger;
    private readonly Action _guideChanged;
    private readonly TimeProvider _time;
    private readonly ScheduleEngine _engine = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Dictionary<string, string> _platforms = new(StringComparer.Ordinal);
    // Each loaded manifest with the platform file it uses, so a platform no manifest uses any more can be forgotten.
    private readonly Dictionary<(string Channel, string Manifest), string?> _loadedManifests = [];
    // _platforms and _loadedManifests are only touched under _refresh. _details is also read by the guide while a
    // refresh may be writing it, hence the concurrent dictionary.
    private readonly ConcurrentDictionary<(string Channel, int Version), IReadOnlyList<ItemDetails>> _details = new();
    private Lineup? _lineup;
    private DateTimeOffset _nextRefreshUtc;
    private Task _backgroundRefresh = Task.CompletedTask;

    /// <summary>Initializes a new instance of the <see cref="SpelunkrSource"/> class.</summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
    /// <param name="taskManager">Jellyfin's task manager, to refresh the guide when a new schedule version appears.</param>
    /// <param name="logger">Logger.</param>
    public SpelunkrSource(IHttpClientFactory httpClientFactory, ITaskManager taskManager, ILogger<SpelunkrSource> logger)
        : this(
            BucketReader(httpClientFactory, ReadLimit),
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

    /// <summary>Reads one file from the data host, giving up after <paramref name="limit"/>.</summary>
    internal static Func<string, CancellationToken, Task<string>> BucketReader(IHttpClientFactory httpClientFactory, TimeSpan limit)
        => async (path, ct) =>
        {
            // CreateClient hands out a new HttpClient each call, so its default headers are ours alone.
            var client = httpClientFactory.CreateClient(NamedClient.Default);
            client.DefaultRequestHeaders.Referrer = SiteReferer;
            // The caller's token stays live when the limit fires, so IsCallerCancel treats it as an ordinary failure.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            return await client.GetStringAsync(Bucket + path, timeout.Token).ConfigureAwait(false);
        };

    /// <summary>
    /// Keeps the lineup fresh with nobody tuned in, until <paramref name="stop"/>: refreshes whenever one is due and
    /// sleeps until the next (10 minutes, or 5 after a failure). Skips while <paramref name="enabled"/> says no.
    /// </summary>
    /// <param name="enabled">Whether to refresh this time (false while Live TV has no Spelunkr tuner).</param>
    /// <param name="delay">Sleeps; Task.Delay in Jellyfin, a fake clock in tests.</param>
    /// <param name="stop">Jellyfin shutting down.</param>
    internal async Task RunRefreshTimerAsync(Func<bool> enabled, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var wait = LineupMaxAge;
            try
            {
                if (enabled())
                {
                    wait = await RefreshIfDueAsync(stop).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                // The timer must outlive any one bad tick.
                _logger.LogWarning(ex, "Spelunkr's lineup refresh timer hit an error");
                wait = RetryAfterFailure;
            }
            catch (Exception)
            {
                return; // Jellyfin is shutting down
            }

            try
            {
                // At least a second, so a clock oddity can never make this spin.
                await delay(TimeSpan.FromTicks(Math.Clamp(wait.Ticks, TimeSpan.TicksPerSecond, LineupMaxAge.Ticks)), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Refreshes the lineup if one is due, and says how long until the next is.</summary>
    internal async Task<TimeSpan> RefreshIfDueAsync(CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() >= _nextRefreshUtc)
        {
            try
            {
                // Under _refresh like every refresh, so this never runs alongside one a caller started.
                await RefreshLineupAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsCallerCancel(ex, cancellationToken))
            {
                // Only the first load gets here (later failures keep the earlier copy); it is retried when due.
                _logger.LogWarning(ex, "Could not load the Spelunkr lineup");
            }
        }

        return _nextRefreshUtc - _time.GetUtcNow();
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

    /// <summary>The films on a channel between two moments (Unix ms), plus the film before and the first one after.</summary>
    public async Task<IReadOnlyList<Segment>> ShowsBetweenAsync(string channelId, long fromMs, long toMs, CancellationToken cancellationToken)
    {
        var lineup = await GetLineupAsync(cancellationToken).ConfigureAwait(false);
        return _engine.ShowsBetween(channelId, fromMs, toMs, lineup.AudioBase);
    }

    private async Task<Lineup> GetLineupAsync(CancellationToken cancellationToken)
    {
        if (_lineup is null)
        {
            return await RefreshLineupAsync(cancellationToken).ConfigureAwait(false);
        }

        // A stale lineup is refreshed behind the caller: a channel opening here must not wait for a batch of new
        // schedule files (14 s for 48 of them), and what is on now is in the copy we have. Two callers can both
        // start one; the second finds the lineup fresh under _refresh and does nothing.
        if (_time.GetUtcNow() >= _nextRefreshUtc && _backgroundRefresh.IsCompleted)
        {
            _backgroundRefresh = Task.Run(() => RefreshLineupAsync(CancellationToken.None), CancellationToken.None);
        }

        return _lineup;
    }

    private async Task<Lineup> RefreshLineupAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lineup is not null && _time.GetUtcNow() < _nextRefreshUtc)
            {
                return _lineup;
            }

            try
            {
                var lineup = ParseLineup(await _read("lineup.json", cancellationToken).ConfigureAwait(false));
                var added = false;
                var anyFailed = false;
                foreach (var channel in lineup.Channels.Where(IsOffered))
                {
                    // One channel's bad or missing file must not take the other channels down with it; this one
                    // is left out (or keeps the versions it had) and is retried in 5 minutes.
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
                        anyFailed = true;
                        continue;
                    }

                    // Only once every listed version is in: forgetting first could leave a channel with nothing.
                    ForgetUnlisted(channel);
                }

                // A platform file stays as long as any loaded manifest uses it.
                foreach (var platform in _platforms.Keys.Where(p => !_loadedManifests.ContainsValue(p)).ToList())
                {
                    _platforms.Remove(platform);
                }

                // The stream follows a new version on its own, but Jellyfin only re-reads the guide when its guide
                // task runs (daily), so ask for a run now. The first load is covered by Registration's refresh.
                if (added && _lineup is not null)
                {
                    _guideChanged();
                }

                _lineup = lineup;
                _nextRefreshUtc = _time.GetUtcNow() + (anyFailed ? RetryAfterFailure : LineupMaxAge);
            }
            catch (Exception ex) when (!IsCallerCancel(ex, cancellationToken))
            {
                _nextRefreshUtc = _time.GetUtcNow() + RetryAfterFailure;
                if (_lineup is null)
                {
                    throw;
                }

                // Keep playing from what we already have; Spelunkr being briefly unreachable shouldn't stop a channel.
                _logger.LogWarning(ex, "Could not refresh the Spelunkr lineup; using the copy from earlier");
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

    /// <summary>
    /// Reads lineup.json one channel at a time, so a malformed channel (even one this plugin doesn't offer) is
    /// skipped instead of taking the whole lineup with it.
    /// </summary>
    private Lineup ParseLineup(string json)
    {
        using var doc = JsonDocument.Parse(json);
        // audioBase is the start of every bump's music address, which goes straight to ffmpeg. Every other remote
        // address is pinned (BucketPath, archive.org), so this one is too: a bad one keeps the earlier lineup.
        var audioBase = doc.RootElement.TryGetProperty("audioBase", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        if (!Uri.TryCreate(audioBase, UriKind.Absolute, out var audioUri) || audioUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"lineup.json's audioBase '{audioBase}' is not an https:// address");
        }

        var channels = new List<LineupChannel>();
        foreach (var element in doc.RootElement.GetProperty("channels").EnumerateArray())
        {
            try
            {
                // Deserialize copies the version pointers out, so they outlive this JsonDocument.
                var channel = element.Deserialize<LineupChannel>(ScheduleEngine.Json);
                channels.Add(channel is { Id: not null, Name: not null, Color: not null, Versions: not null }
                    ? channel
                    : throw new InvalidDataException("a channel is missing its id, name, color or versions"));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                _logger.LogWarning(ex, "Skipped malformed Spelunkr channel {Channel}", element.TryGetProperty("id", out var id) ? id.ToString() : "(no id)");
            }
        }

        return new Lineup(audioBase, channels);
    }

    /// <summary>
    /// Forgets the versions of a channel the lineup no longer lists (the site does the same on reload): out of the
    /// engine, and their manifests and details out of the caches. Without this, every version published while
    /// Jellyfin runs stays in memory.
    /// </summary>
    /// <remarks>ponytail: a channel dropped from the lineup altogether keeps its versions until a restart.</remarks>
    private void ForgetUnlisted(LineupChannel channel)
    {
        var manifests = channel.Versions.Select(p => BucketPath(p.GetProperty("manifest").GetString())).ToHashSet(StringComparer.Ordinal);
        var versions = channel.Versions.Select(p => p.GetProperty("v").GetInt32()).ToHashSet();
        _engine.KeepVersions(channel.Id, versions);
        foreach (var key in _loadedManifests.Keys.Where(k => k.Channel == channel.Id && !manifests.Contains(k.Manifest)).ToList())
        {
            _loadedManifests.Remove(key);
        }

        foreach (var key in _details.Keys.Where(k => k.Channel == channel.Id && !versions.Contains(k.Version)))
        {
            _details.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Loads one version into the engine. True when something the guide shows changed: a new version, or the
    /// details file of a loaded one that failed to download before.
    /// </summary>
    private async Task<bool> LoadVersionAsync(string channelId, JsonElement pointer, CancellationToken cancellationToken)
    {
        var manifestPath = BucketPath(pointer.GetProperty("manifest").GetString());
        if (_loadedManifests.ContainsKey((channelId, manifestPath)))
        {
            return !_details.ContainsKey((channelId, pointer.GetProperty("v").GetInt32()))
                && await LoadDetailsAsync(channelId, pointer, cancellationToken).ConfigureAwait(false);
        }

        var manifest = await _read(manifestPath, cancellationToken).ConfigureAwait(false);
        string? platform = null;
        string? platformPath = null;
        using (var doc = JsonDocument.Parse(manifest))
        {
            if (doc.RootElement.TryGetProperty("platform", out var p) && p.GetString() is { Length: > 0 } platformRef)
            {
                platformPath = BucketPath(platformRef);
                if (!_platforms.TryGetValue(platformPath, out platform))
                {
                    platform = await _read(platformPath, cancellationToken).ConfigureAwait(false);
                    _platforms[platformPath] = platform;
                }
            }
        }

        _engine.AddVersion(channelId, manifest, platform, pointer.GetRawText());
        await LoadDetailsAsync(channelId, pointer, cancellationToken).ConfigureAwait(false);
        _loadedManifests[(channelId, manifestPath)] = platformPath;
        _logger.LogInformation("Loaded Spelunkr schedule {Manifest}", manifestPath);
        return true;
    }

    /// <summary>
    /// True only when the caller asked to stop. HttpClient reports its own timeout as a cancellation too; that one
    /// is an ordinary fetch failure and must be handled like any other.
    /// </summary>
    private static bool IsCallerCancel(Exception ex, CancellationToken cancellationToken)
        => ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>
    /// Loads a version's details file (film descriptions for the guide); true if it loaded. Optional: a failure only
    /// costs descriptions until the next refresh tries again.
    /// </summary>
    private async Task<bool> LoadDetailsAsync(string channelId, JsonElement pointer, CancellationToken cancellationToken)
    {
        if (!pointer.TryGetProperty("details", out var d) || d.GetString() is not { Length: > 0 } path)
        {
            return false;
        }

        try
        {
            var details = JsonSerializer.Deserialize<DetailsFile>(await _read(BucketPath(path), cancellationToken).ConfigureAwait(false), ScheduleEngine.Json);
            _details[(channelId, pointer.GetProperty("v").GetInt32())] = details?.Items ?? [];
            return true;
        }
        catch (Exception ex) when (!IsCallerCancel(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "Could not load Spelunkr film details {Path}; the guide shows titles only until the next refresh", path);
            return false;
        }
    }

    private sealed record DetailsFile(IReadOnlyList<ItemDetails> Items);
}

/// <summary>Spelunkr's write-up of one film, from a version's details file (same order as the manifest's items).</summary>
public sealed record ItemDetails(string? Description, string? Director, IReadOnlyList<string>? Stars);

/// <summary>Spelunkr's lineup.json (only the fields this plugin reads).</summary>
public sealed record Lineup(string AudioBase, IReadOnlyList<LineupChannel> Channels);

/// <summary>One channel in the lineup.</summary>
public sealed record LineupChannel(
    string Id,
    string Name,
    string Color,
    int? Dial,
    IReadOnlyList<JsonElement> Versions,
    bool ComingSoon = false,
    bool DebugOnly = false,
    bool AudioOverlay = false,
    Logomark? Logomark = null);

/// <summary>A channel's pixel-art logo: square rows of palette letters, "." for transparent.</summary>
public sealed record Logomark(string Type, IReadOnlyList<string>? Rows);
