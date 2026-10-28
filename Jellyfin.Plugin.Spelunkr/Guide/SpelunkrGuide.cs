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

        var id = channelId[SpelunkrTunerHost.ChannelIdPrefix.Length..];
        var fromMs = new DateTimeOffset(startDateUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(endDateUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var segments = (await _source.SegmentsBetweenAsync(id, fromMs, toMs, cancellationToken).ConfigureAwait(false)).ToList();

        // A window that starts or ends during a break would cut the films at its edges (an entry runs from a film's
        // start to the next film's start). So reach back to the film before the window and on to the first film
        // that starts after it, then keep the entries that overlap the window.
        // ponytail: capped at 50 steps each way; a channel with no films for 50 segments shows a partial edge.
        for (var i = 0; i < 50 && segments.Count > 0 && segments[0].Kind != "show"; i++)
        {
            segments.Insert(0, await _source.SegmentAtAsync(id, segments[0].StartMs - 1, cancellationToken).ConfigureAwait(false));
        }

        for (var i = 0; i < 50 && segments.Count > 0 && !segments.Any(s => s.Kind == "show" && s.StartMs >= toMs); i++)
        {
            segments.Add(await _source.SegmentAtAsync(id, segments[^1].EndMs, cancellationToken).ConfigureAwait(false));
        }

        var films = (await _source.GetChannelsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(c => c.Id == id)?.Family == "film";
        return GuidePrograms.Build(segments, channelId, s => _source.Details(id, s), films).Where(p => p.EndDate > startDateUtc && p.StartDate < endDateUtc).ToList();
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
