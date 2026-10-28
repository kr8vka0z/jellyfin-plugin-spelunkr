// SpelunkrTunerHost: the Live TV "tuner" Jellyfin asks for channels and streams. Jellyfin asks every tuner
// about every channel, so channels that aren't ours must be refused in exactly the way Jellyfin expects.
using System.Globalization;
using Jellyfin.Plugin.Spelunkr.Stream;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr;

/// <summary>Offers Spelunkr channels to Jellyfin Live TV.</summary>
public sealed class SpelunkrTunerHost : ITunerHost
{
    /// <summary>The tuner type saved in Jellyfin's Live TV settings.</summary>
    public const string TunerType = "spelunkr";

    /// <summary>Prefix on every Jellyfin channel id this plugin owns ("spelunkr_gumshoe").</summary>
    public const string ChannelIdPrefix = "spelunkr_";

    private readonly SpelunkrSource _source;
    private readonly IConfigurationManager _config;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<SpelunkrTunerHost> _logger;

    /// <summary>Initializes a new instance of the <see cref="SpelunkrTunerHost"/> class.</summary>
    public SpelunkrTunerHost(SpelunkrSource source, IConfigurationManager config, IMediaEncoder mediaEncoder, IServerApplicationHost appHost, ILogger<SpelunkrTunerHost> logger)
    {
        _source = source;
        _config = config;
        _mediaEncoder = mediaEncoder;
        _appHost = appHost;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Spelunkr";

    /// <inheritdoc />
    public string Type => TunerType;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        // Only report channels while our tuner entry exists in Live TV settings (Registration adds it), so a user
        // who deletes it there sees the channels go. The guide also needs the entry's id on each channel.
        var tuner = Registration.FindTuner(_config);
        if (tuner is null)
        {
            return [];
        }

        var channels = await _source.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        return channels.Select(c => ToChannelInfo(c, tuner.Id)).ToList();
    }

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        => Task.FromResult(channelId.StartsWith(ChannelIdPrefix, StringComparison.Ordinal) ? [NewMediaSource(channelId)] : new List<MediaSourceInfo>());

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // FileNotFoundException is how a tuner says "not mine" (DefaultLiveTvService moves on to the next tuner).
        if (!channelId.StartsWith(ChannelIdPrefix, StringComparison.Ordinal))
        {
            throw new FileNotFoundException();
        }

        var id = channelId[ChannelIdPrefix.Length..];
        var channel = (await _source.GetChannelsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(c => c.Id == id)
            ?? throw new FileNotFoundException($"Spelunkr channel {id} is not offered");
        var tuner = Registration.FindTuner(_config) ?? throw new FileNotFoundException("Spelunkr tuner is not set up");

        var stream = new ChannelStream(id, new ChannelLook(channel.Name, channel.Color), NewMediaSource(channelId), tuner.Id, _source, _mediaEncoder.EncoderPath, StreamsDir(_config), _appHost, _logger);
        try
        {
            await stream.Open(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.Close().ConfigureAwait(false);
            stream.Dispose();
            throw;
        }

        return stream;
    }

    /// <inheritdoc />
    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
        => Task.FromResult(new List<TunerHostInfo>());

    /// <summary>Where open streams keep their files. Emptied at startup by Registration.</summary>
    internal static string StreamsDir(IConfigurationManager config) => Path.Combine(config.CommonApplicationPaths.CachePath, "spelunkr-streams");

    internal static ChannelInfo ToChannelInfo(LineupChannel c, string tunerHostId) => new()
    {
        Id = ChannelIdPrefix + c.Id,
        TunerChannelId = c.Id,
        Name = c.Name,
        Number = c.Dial.ToString(CultureInfo.InvariantCulture),
        ChannelType = ChannelType.TV,
        TunerHostId = tunerHostId,
    };

    // Shaped like Jellyfin's own M3U tuner: stream details unknown (Index -1), so Jellyfin probes the stream
    // once it is open. RequiresOpening makes Jellyfin call GetChannelStream before playing.
    private static MediaSourceInfo NewMediaSource(string channelId) => new()
    {
        Id = channelId,
        Path = string.Empty,
        Protocol = MediaProtocol.Http,
        MediaStreams =
        [
            new MediaStream { Type = MediaStreamType.Video, Index = -1, IsInterlaced = false },
            new MediaStream { Type = MediaStreamType.Audio, Index = -1 },
        ],
        RequiresOpening = true,
        RequiresClosing = true,
        IsInfiniteStream = true,
        IsRemote = false,
        SupportsDirectPlay = false,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
    };
}
