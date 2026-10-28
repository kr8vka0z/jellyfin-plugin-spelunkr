// GuidePrograms: turns a stretch of schedule into TV-guide entries, one per film.
using System.Globalization;
using Jellyfin.Plugin.Spelunkr.Schedule;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.Spelunkr.Guide;

/// <summary>Builds guide entries from schedule segments.</summary>
internal static class GuidePrograms
{
    /// <summary>
    /// One entry per film, running until the next film starts. Ads and bumps (a few minutes an hour) are folded
    /// into the film before them rather than listed: the guide stays readable and still lines up with the stream.
    /// </summary>
    public static List<ProgramInfo> Build(IReadOnlyList<Segment> segments, string channelId)
    {
        var shows = segments.Where(s => s.Kind == "show").ToList();
        var programs = new List<ProgramInfo>(shows.Count);
        for (var i = 0; i < shows.Count; i++)
        {
            var s = shows[i];
            var endMs = i + 1 < shows.Count ? shows[i + 1].StartMs : s.EndMs;
            programs.Add(new ProgramInfo
            {
                // version-cycle-index names one slot in the schedule, so a refresh updates rather than duplicates it.
                Id = string.Create(CultureInfo.InvariantCulture, $"{channelId}-{s.Version}-{s.Cycle}-{s.Index}"),
                ChannelId = channelId,
                Name = s.Title ?? s.Id,
                StartDate = DateTimeOffset.FromUnixTimeMilliseconds(s.StartMs).UtcDateTime,
                EndDate = DateTimeOffset.FromUnixTimeMilliseconds(endMs).UtcDateTime,
                ProductionYear = int.TryParse(s.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null,
                IsMovie = true,
            });
        }

        return programs;
    }
}
