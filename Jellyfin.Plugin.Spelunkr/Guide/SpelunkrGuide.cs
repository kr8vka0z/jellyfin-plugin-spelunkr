// SpelunkrGuide: the Live TV guide source ("listings provider") that fills in what's on for Spelunkr channels.
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.Spelunkr.Guide;

/// <summary>Guide data computed from Spelunkr's schedule, no download of a guide file needed.</summary>
public sealed class SpelunkrGuide : IListingsProvider
{
    /// <summary>The guide source type saved in Jellyfin's Live TV settings.</summary>
    public const string ProviderType = "spelunkr";

    private readonly SpelunkrSource _source;

    /// <summary>Initializes a new instance of the <see cref="SpelunkrGuide"/> class.</summary>
    public SpelunkrGuide(SpelunkrSource source) => _source = source;

    /// <inheritdoc />
    public string Name => "Spelunkr";

    /// <inheritdoc />
    public string Type => ProviderType;

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        if (!channelId.StartsWith(SpelunkrTunerHost.ChannelIdPrefix, StringComparison.Ordinal))
        {
            return [];
        }

        var segments = await _source.SegmentsBetweenAsync(
            channelId[SpelunkrTunerHost.ChannelIdPrefix.Length..],
            new DateTimeOffset(startDateUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            new DateTimeOffset(endDateUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            cancellationToken).ConfigureAwait(false);
        return GuidePrograms.Build(segments, channelId);
    }

    /// <inheritdoc />
    public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location) => Task.FromResult(new List<NameIdPair>());

    /// <inheritdoc />
    public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
    {
        // Same ids as the tuner's channels: Jellyfin matches guide channels to tuner channels by id first.
        var channels = await _source.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        return channels.Select(c => SpelunkrTunerHost.ToChannelInfo(c, string.Empty)).ToList();
    }
}
