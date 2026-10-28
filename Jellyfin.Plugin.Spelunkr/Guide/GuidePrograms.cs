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
    /// <param name="segments">Schedule segments, in order.</param>
    /// <param name="channelId">Jellyfin channel id.</param>
    /// <param name="details">Spelunkr's write-up of a film, if known.</param>
    /// <param name="films">Whether the channel shows feature films (marked as movies in the guide) rather than episodes and shorts.</param>
    public static List<ProgramInfo> Build(IReadOnlyList<Segment> segments, string channelId, Func<Segment, ItemDetails?>? details = null, bool films = true)
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
                IsMovie = films,
                Overview = Overview(details?.Invoke(s)),
                // The poster spelunkr.tv itself shows for an archive.org item.
                ImageUrl = "https://archive.org/services/img/" + Uri.EscapeDataString(s.Id),
            });
        }

        return programs;
    }

    /// <summary>Description, then "Directed by X. With A, B." when the details file has them.</summary>
    private static string? Overview(ItemDetails? d)
    {
        if (d is null)
        {
            return null;
        }

        var credits = string.Join(
            " ",
            new[]
            {
                string.IsNullOrWhiteSpace(d.Director) ? null : $"Directed by {d.Director}.",
                d.Stars is { Count: > 0 } ? $"With {string.Join(", ", d.Stars)}." : null,
            }.Where(x => x is not null));
        var text = string.Join("\n\n", new[] { d.Description, credits }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return text.Length > 0 ? text : null;
    }
}
