// LiveTvLandingTests: the Live TV landing fill sets Guide only where no choice exists, keeps every other
// preference, skips a failing account without stopping, and does nothing when switched off.
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class LiveTvLandingTests
{
    private static readonly Guid Empty = Guid.NewGuid();
    private static readonly Guid HasOthers = Guid.NewGuid();
    private static readonly Guid PickedPrograms = Guid.NewGuid();
    private static readonly Guid Broken = Guid.NewGuid();

    private static FakePrefs Seeded()
    {
        var prefs = new FakePrefs { Failing = Broken };
        prefs.Store[HasOthers] = new() { ["homesection0"] = "smalllibrarytiles", ["landing-movies"] = "suggestions" };
        prefs.Store[PickedPrograms] = new() { ["landing-livetv"] = "programs" };
        return prefs;
    }

    [Fact]
    public void Fills_only_missing_choices_and_keeps_the_rest()
    {
        var prefs = Seeded();

        // Broken comes first: its failure must not stop the others.
        LiveTvLanding.Apply(true, [Broken, Empty, HasOthers, PickedPrograms], prefs, NullLogger.Instance);

        Assert.Equal(new Dictionary<string, string?> { ["landing-livetv"] = "guide" }, prefs.Store[Empty]);
        Assert.Equal(
            new Dictionary<string, string?> { ["homesection0"] = "smalllibrarytiles", ["landing-movies"] = "suggestions", ["landing-livetv"] = "guide" },
            prefs.Store[HasOthers]);
        Assert.Equal(new Dictionary<string, string?> { ["landing-livetv"] = "programs" }, prefs.Store[PickedPrograms]);
        Assert.Equal(2, prefs.Writes);
    }

    [Fact]
    public void Off_changes_nobody()
    {
        var prefs = Seeded();

        LiveTvLanding.Apply(false, [Empty, HasOthers, PickedPrograms], prefs, NullLogger.Instance);

        Assert.Equal(0, prefs.Writes);
        Assert.False(prefs.Store.ContainsKey(Empty));
    }

    [Fact]
    public void Uses_the_row_jellyfin_web_reads()
    {
        // MD5 of "usersettings" as Jellyfin's DisplayPreferencesController computes it; pinned so a change to
        // that mapping fails here instead of silently writing a row nobody reads.
        Assert.Equal(Guid.Parse("3ce5b65d-e116-d731-65d1-efc4a30ec35c"), LiveTvLanding.UserSettingsItemId);
    }

    // Only the custom preferences of the one item and client the fill uses; Set replaces the whole set, like Jellyfin's.
    private sealed class FakePrefs : IDisplayPreferencesManager
    {
        public Dictionary<Guid, Dictionary<string, string?>> Store { get; } = [];

        public Guid Failing { get; init; }

        public int Writes { get; private set; }

        public Dictionary<string, string?> ListCustomItemDisplayPreferences(Guid userId, Guid itemId, string client)
        {
            Assert.Equal(LiveTvLanding.UserSettingsItemId, itemId);
            Assert.Equal("emby", client);
            if (userId == Failing)
            {
                throw new InvalidOperationException("database is locked");
            }

            return Store.TryGetValue(userId, out var prefs) ? new(prefs) : [];
        }

        public void SetCustomItemDisplayPreferences(Guid userId, Guid itemId, string client, Dictionary<string, string?> customPreferences)
        {
            Writes++;
            Store[userId] = new(customPreferences);
        }

        public DisplayPreferences GetDisplayPreferences(Guid userId, Guid itemId, string client) => throw new NotSupportedException();

        public ItemDisplayPreferences GetItemDisplayPreferences(Guid userId, Guid itemId, string client) => throw new NotSupportedException();

        public IList<ItemDisplayPreferences> ListItemDisplayPreferences(Guid userId, string client) => throw new NotSupportedException();

        public void UpdateDisplayPreferences(DisplayPreferences displayPreferences) => throw new NotSupportedException();

        public void UpdateItemDisplayPreferences(ItemDisplayPreferences itemDisplayPreferences) => throw new NotSupportedException();
    }
}
