// SpelunkrSource: fetches Spelunkr's public lineup, manifests and platform files, keeps the schedule engine
// loaded with every version the lineup lists, and answers schedule questions for the tuner and the guide.
using System.Text.Json;
using Jellyfin.Plugin.Spelunkr.Schedule;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr;

/// <summary>Spelunkr's data plus the schedule engine, shared by the tuner and the guide.</summary>
public sealed class SpelunkrSource
{
    /// <summary>Spelunkr's public storage bucket (no login needed).</summary>
    public const string Bucket = "https://jbuopkuluyfjwgsrigry.supabase.co/storage/v1/object/public/broadcast/";

    // ponytail: chunk 1 offers Gumshoe only. Everything below is per-channel; widening this list is the switch.
    private static readonly string[] EnabledChannels = ["gumshoe"];

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
    private readonly HashSet<string> _loadedManifests = new(StringComparer.Ordinal);
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
        return lineup.Channels.Where(c => EnabledChannels.Contains(c.Id)).ToList();
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
                foreach (var channel in lineup.Channels.Where(c => EnabledChannels.Contains(c.Id)))
                {
                    foreach (var pointer in channel.Versions)
                    {
                        added |= await LoadVersionAsync(channel.Id, pointer, cancellationToken).ConfigureAwait(false);
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
            catch (Exception ex) when (_lineup is not null && ex is not OperationCanceledException)
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

    /// <summary>Loads one version into the engine; false if it was already loaded.</summary>
    private async Task<bool> LoadVersionAsync(string channelId, JsonElement pointer, CancellationToken cancellationToken)
    {
        var manifestPath = pointer.GetProperty("manifest").GetString()
            ?? throw new InvalidDataException($"{channelId}: version without a manifest path");
        if (_loadedManifests.Contains(manifestPath))
        {
            return false;
        }

        var manifest = await _read(manifestPath, cancellationToken).ConfigureAwait(false);
        string? platform = null;
        using (var doc = JsonDocument.Parse(manifest))
        {
            if (doc.RootElement.TryGetProperty("platform", out var p) && p.GetString() is { Length: > 0 } platformPath)
            {
                if (!_platforms.TryGetValue(platformPath, out platform))
                {
                    platform = await _read(platformPath, cancellationToken).ConfigureAwait(false);
                    _platforms[platformPath] = platform;
                }
            }
        }

        _engine.AddVersion(channelId, manifest, platform, pointer.GetRawText());
        _loadedManifests.Add(manifestPath);
        _logger.LogInformation("Loaded Spelunkr schedule {Manifest}", manifestPath);
        return true;
    }
}

/// <summary>Spelunkr's lineup.json (only the fields this plugin reads).</summary>
public sealed record Lineup(string AudioBase, int Engine, IReadOnlyList<LineupChannel> Channels);

/// <summary>One channel in the lineup.</summary>
public sealed record LineupChannel(string Id, string Name, string Color, int Dial, IReadOnlyList<JsonElement> Versions);
