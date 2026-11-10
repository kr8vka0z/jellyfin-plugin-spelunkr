// BumpCards: when each line of text appears during a bump, following the engine's layout constants.
using Jellyfin.Plugin.Spelunkr.Schedule;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>Text on screen from <see cref="StartMs"/> to <see cref="EndMs"/>, measured from the bump's start.</summary>
/// <param name="StartMs">When it shows.</param>
/// <param name="EndMs">When it goes.</param>
/// <param name="Text">The words.</param>
/// <param name="Color">The text color ("#RRGGBB") when it isn't the one chosen for the channel color.</param>
/// <param name="Outline">A 2 px outline color around the words, if any.</param>
public sealed record BumpCard(long StartMs, long EndMs, string Text, string? Color = null, string? Outline = null);

/// <summary>Lays out bump slides on a timeline.</summary>
public static class BumpCards
{
    /// <summary>
    /// open/full: lead-in (channel name shown), channel slides, tail; full adds a gap, the network slides and a
    /// second tail. close: the network slides, then the tail. Tails and the gap show the plain color.
    /// </summary>
    public static IReadOnlyList<BumpCard> For(Segment bump, ChannelLook look) => Lay(bump, look, null);

    /// <summary>
    /// The cards for the job that draws <see cref="Segment.Clip"/>: the channel slides take the site's timing (the clip's
    /// own, see schedule.js), words hidden while the clip moves are left out, and words over the clip take the site's
    /// colors. The channel half lasts as long as in <see cref="For"/>, so the network half is unchanged.
    /// </summary>
    public static IReadOnlyList<BumpCard> WithClip(Segment bump, ChannelLook look) => Lay(bump, look, bump.Clip);

    private static List<BumpCard> Lay(Segment bump, ChannelLook look, BumpClip? clip)
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
            if (clip is null)
            {
                Slides(bump.Channel);
            }
            else
            {
                ClipSlides(bump.Channel, clip, look, cards);
                t += bump.Channel.Slides.Sum(s => s.Ms);
            }

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

    // Over the clip the site draws the words in the channel color; on a duotone's inverted hold steps they turn black
    // with a channel-color outline.
    private static void ClipSlides(BumpPart channel, BumpClip clip, ChannelLook look, List<BumpCard> cards)
    {
        var inverts = clip.Look is "duotone" or "duozoom";
        for (var i = 0; i < channel.Slides.Count && i < clip.Slides.Count; i++)
        {
            var (s, at) = (channel.Slides[i], clip.Slides[i]);
            if (at.Hidden)
            {
                continue;
            }

            var card = new BumpCard(at.StartMs, at.EndMs, s.Text);
            cards.Add(i < clip.First ? card
                : inverts && at.HoldStep % 2 == 1 ? card with { Color = "#000000", Outline = look.Color }
                : card with { Color = look.Color });
        }
    }
}
