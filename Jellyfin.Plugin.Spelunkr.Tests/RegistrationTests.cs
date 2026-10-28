// RegistrationTests: the Spelunkr guide entry in Live TV settings always points at the current Spelunkr tuner,
// including after a user deletes the tuner and a restart recreates it with a new id.
using Jellyfin.Plugin.Spelunkr.Guide;
using MediaBrowser.Model.LiveTv;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class RegistrationTests
{
    [Fact]
    public void Adds_a_guide_entry_for_the_tuner_when_there_is_none()
    {
        var options = new LiveTvOptions { ListingProviders = [new ListingsProviderInfo { Type = "xmltv", EnableAllTuners = true }] };

        Assert.True(Registration.EnsureGuideEntry(options, "tuner-1"));

        var guide = Assert.Single(options.ListingProviders, g => g.Type == SpelunkrGuide.ProviderType);
        Assert.Equal(["tuner-1"], guide.EnabledTuners);
        Assert.False(guide.EnableAllTuners);
        Assert.Equal(2, options.ListingProviders.Length);
    }

    [Fact]
    public void Repoints_the_guide_at_a_recreated_tuner()
    {
        // The user deleted the Spelunkr tuner; on restart a new one was created with a new id.
        var options = new LiveTvOptions { ListingProviders = [new ListingsProviderInfo { Id = "g", Type = SpelunkrGuide.ProviderType, EnableAllTuners = false, EnabledTuners = ["old-tuner"] }] };

        Assert.True(Registration.EnsureGuideEntry(options, "new-tuner"));

        Assert.Equal(["new-tuner"], Assert.Single(options.ListingProviders).EnabledTuners);
    }

    [Fact]
    public void Leaves_a_correct_entry_alone()
    {
        var options = new LiveTvOptions { ListingProviders = [new ListingsProviderInfo { Id = "g", Type = SpelunkrGuide.ProviderType, EnableAllTuners = false, EnabledTuners = ["t"] }] };

        Assert.False(Registration.EnsureGuideEntry(options, "t"));
    }
}
