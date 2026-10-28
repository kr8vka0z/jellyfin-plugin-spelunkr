// BumpCards: when each line of text appears during a bump, following the engine's layout constants.
using Jellyfin.Plugin.Spelunkr.Schedule;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>Text on screen from <see cref="StartMs"/> to <see cref="EndMs"/>, measured from the bump's start.</summary>
public sealed record BumpCard(long StartMs, long EndMs, string Text);

/// <summary>Lays out bump slides on a timeline.</summary>
public static class BumpCards
{
    /// <summary>
    /// open/full: lead-in (channel name shown), channel slides, tail; full adds a gap, the network slides and a
    /// second tail. close: the network slides, then the tail. Tails and the gap show the plain color.
    /// </summary>
    public static IReadOnlyList<BumpCard> For(Segment bump, ChannelLook look)
    {
        var layout = bump.Layout ?? new BumpLayout(0, 0, 0);
        var cards = new List<BumpCard>();
        var t = 0L;

        void Slides(BumpPart part)
        {
            foreach (var s in part.Slides)
            {
                cards.Add(new BumpCard(t, t + s.Ms, s.Text));
                t += s.Ms;
            }
        }

        if (bump.Channel is not null)
        {
            // The site animates the channel's art during the lead-in; a name card stands in for it here.
            cards.Add(new BumpCard(0, layout.LeadInMs, look.Name));
            t = layout.LeadInMs;
            Slides(bump.Channel);
            t += layout.TailMs;
        }

        if (bump.Platform is not null)
        {
            if (bump.Channel is not null)
            {
                t += layout.GapMs;
            }

            Slides(bump.Platform);
        }

        return cards;
    }
}
