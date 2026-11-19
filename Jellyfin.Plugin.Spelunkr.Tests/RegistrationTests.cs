// RegistrationTests: the Spelunkr guide entry in Live TV settings always points at the current Spelunkr tuner,
// including after a user deletes the tuner and a restart recreates it with a new id.
using Jellyfin.Plugin.Spelunkr.Guide;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task Startup_starts_the_lineup_refresh_timer_and_shutdown_stops_it()
    {
        var root = Directory.CreateTempSubdirectory("spelunkr-registration").FullName;
        try
        {
            var paths = TunerRefusalTests.Stub<IApplicationPaths>.Of(m => m.Name == "get_CachePath" ? root : null);
            var config = TunerRefusalTests.Stub<IConfigurationManager>.Of(m => m.Name switch
            {
                "get_CommonApplicationPaths" => paths,
                "GetConfiguration" => new LiveTvOptions { TunerHosts = [new TunerHostInfo { Id = "t", Type = SpelunkrTunerHost.TunerType }] },
                _ => null,
            });
            var tasks = TunerRefusalTests.Stub<ITaskManager>.Of(m => m.Name == "get_ScheduledTasks" ? Array.Empty<IScheduledTaskWorker>() : null);
            var lineupRead = new TaskCompletionSource();
            var source = new SpelunkrSource(
                (path, _) =>
                {
                    if (path == "lineup.json")
                    {
                        lineupRead.TrySetResult();
                    }

                    return Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path)));
                },
                NullLogger<SpelunkrSource>.Instance);
            var registration = new Registration(TunerRefusalTests.Stub<ITunerHostManager>.Of(_ => null), config, tasks, source, NullLogger<Registration>.Instance);

            await registration.StartAsync(CancellationToken.None);
            await lineupRead.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); // nobody tuned in

            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await registration.StopAsync(shutdown.Token); // returns because the timer ended, not because the token ran out
            Assert.False(shutdown.IsCancellationRequested);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Leaves_a_correct_entry_alone()
    {
        var options = new LiveTvOptions { ListingProviders = [new ListingsProviderInfo { Id = "g", Type = SpelunkrGuide.ProviderType, EnableAllTuners = false, EnabledTuners = ["t"] }] };

        Assert.False(Registration.EnsureGuideEntry(options, "t"));
    }
}
