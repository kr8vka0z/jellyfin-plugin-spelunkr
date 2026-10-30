// SpelunkrTunerHost: the Live TV "tuner" Jellyfin asks for channels and streams. Jellyfin asks every tuner
// about every channel, so channels that aren't ours must be refused in exactly the way Jellyfin expects.
using System.Globalization;
using Jellyfin.Plugin.Spelunkr.Stream;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
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
    private readonly ArchiveOriginals _originals;
    private readonly IConfigurationManager _config;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<SpelunkrTunerHost> _logger;

    /// <summary>Initializes a new instance of the <see cref="SpelunkrTunerHost"/> class.</summary>
    public SpelunkrTunerHost(SpelunkrSource source, ArchiveOriginals originals, IConfigurationManager config, IMediaEncoder mediaEncoder, IServerApplicationHost appHost, ILogger<SpelunkrTunerHost> logger)
    {
        _source = source;
        _originals = originals;
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
        var logos = Path.Combine(_config.CommonApplicationPaths.CachePath, "spelunkr-logos");
        return channels.Select(c =>
        {
            var info = ToChannelInfo(c, tuner.Id);
            info.ImagePath = LogoFile(logos, c, _logger);
            return info;
        }).ToList();
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

        var picture = PictureFor(_config.GetEncodingOptions(), _mediaEncoder.IsVaapiDeviceSupportVulkanDrmInterop, _logger);
        var stream = new ChannelStream(id, new ChannelLook(channel.Name, channel.Color), picture, NewMediaSource(channelId), tuner.Id, _source, _originals, _mediaEncoder.EncoderPath, StreamsDir(_config), _appHost, _logger);
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

    /// <summary>
    /// The channel's logo as a PNG file Jellyfin reads directly, drawn on first use. The name carries a hash of
    /// the logo, so a redrawn logo gets a new file. Re-drawn whenever missing (the cache folder can be emptied).
    /// The file name is the hash alone: the channel id comes from Spelunkr's data and never goes into a path.
    /// </summary>
    internal static string? LogoFile(string dir, LineupChannel c, ILogger logger)
    {
        if (c.Logomark is not { Type: "pixel", Rows: { Count: > 0 } rows })
        {
            return null;
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.Color + string.Concat(rows))))[..16];
        var path = Path.Combine(dir, $"{hash}.png");
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, Guide.ChannelLogo.RenderPng(rows, c.Color));
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the logo for Spelunkr channel {Channel}", c.Id);
            return null;
        }
    }

    /// <summary>
    /// The stream format, following Jellyfin's own hardware acceleration setting (nothing to set up in the plugin):
    /// 1080p on the GPU when Jellyfin uses VA-API with hardware encoding on, otherwise 720p on the CPU.
    /// </summary>
    /// <remarks>
    /// The GPU path needs Vulkan on top of VA-API (libplacebo); Jellyfin tests that at startup
    /// (<paramref name="vulkanInterop"/>), and without it every GPU job would fail and fall back to the CPU at 1080p,
    /// which is heavier than plain 720p. ponytail: VA-API only (AMD and Intel on Linux); NVENC, QSV and others take
    /// the CPU path.
    /// </remarks>
    internal static Picture PictureFor(EncodingOptions options, bool vulkanInterop, ILogger logger)
    {
        if (options.HardwareAccelerationType != HardwareAccelerationType.vaapi)
        {
            return Picture.Cpu;
        }

        var why = !options.EnableHardwareEncoding ? "hardware encoding is switched off"
            : string.IsNullOrEmpty(options.VaapiDevice) ? "no VA-API device is set"
            : !vulkanInterop ? "Jellyfin found the VA-API device can't share frames with Vulkan"
            : null;
        if (why is null)
        {
            return Picture.Gpu(options.VaapiDevice);
        }

        // Logged so a server set to VA-API that still gets 720p says why in Jellyfin's log.
        logger.LogInformation("Spelunkr: streaming at 720p on the CPU because {Reason}", why);
        return Picture.Cpu;
    }

    internal static ChannelInfo ToChannelInfo(LineupChannel c, string tunerHostId) => new()
    {
        Id = ChannelIdPrefix + c.Id,
        TunerChannelId = c.Id,
        Name = c.Name,
        Number = c.Dial?.ToString(CultureInfo.InvariantCulture),
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
