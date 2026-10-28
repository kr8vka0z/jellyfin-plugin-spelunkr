// Registration: on startup, adds the Spelunkr tuner and guide source to Jellyfin's Live TV settings, so the
// channels show up with no setup. Jellyfin only turns Live TV on when a tuner entry exists, and only asks a guide
// source for programs when a guide entry of its type exists, so both entries are needed. Pattern taken from
// the Jellyfin-Xtream-Library plugin's NativeTunerRegistration.
using System.Globalization;
using Jellyfin.Plugin.Spelunkr.Guide;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr;

/// <summary>Makes sure Live TV has a Spelunkr tuner and guide source.</summary>
public sealed class Registration : IHostedService
{
    private const string LiveTvConfigKey = "livetv";

    // RefreshGuideScheduledTask.Key in Jellyfin.LiveTv, which plugins can't reference.
    private const string RefreshGuideTaskKey = "RefreshGuide";

    private readonly ITunerHostManager _tunerHostManager;
    private readonly IConfigurationManager _config;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<Registration> _logger;

    /// <summary>Initializes a new instance of the <see cref="Registration"/> class.</summary>
    public Registration(ITunerHostManager tunerHostManager, IConfigurationManager config, ITaskManager taskManager, ILogger<Registration> logger)
    {
        _tunerHostManager = tunerHostManager;
        _config = config;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>The Spelunkr tuner entry in Live TV settings, if there is one.</summary>
    internal static TunerHostInfo? FindTuner(IConfigurationManager config)
        => config.GetConfiguration<LiveTvOptions>(LiveTvConfigKey).TunerHosts?
            .FirstOrDefault(t => string.Equals(t.Type, SpelunkrTunerHost.TunerType, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // No stream is open yet at startup, so anything here is left over from before a crash or restart.
        var streams = SpelunkrTunerHost.StreamsDir(_config);
        try
        {
            if (Directory.Exists(streams))
            {
                Directory.Delete(streams, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not clear old Spelunkr stream files in {Path}", streams);
        }

        try
        {
            var added = false;
            var tuner = FindTuner(_config);
            if (tuner is null)
            {
                tuner = await _tunerHostManager.SaveTunerHost(
                    new TunerHostInfo { Type = SpelunkrTunerHost.TunerType, FriendlyName = "Spelunkr", Url = "https://spelunkr.tv" },
                    dataSourceChanged: false).ConfigureAwait(false);
                _logger.LogInformation("Added the Spelunkr tuner to Live TV");
                added = true;
            }

            var options = _config.GetConfiguration<LiveTvOptions>(LiveTvConfigKey);
            var guides = options.ListingProviders ?? [];
            if (!guides.Any(g => string.Equals(g.Type, SpelunkrGuide.ProviderType, StringComparison.OrdinalIgnoreCase)))
            {
                options.ListingProviders =
                [
                    .. guides,
                    new ListingsProviderInfo
                    {
                        Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                        Type = SpelunkrGuide.ProviderType,
                        // Only our tuner: other tuners' channels never match Spelunkr's schedule.
                        EnableAllTuners = false,
                        EnabledTuners = [tuner.Id],
                    },
                ];
                _config.SaveConfiguration(LiveTvConfigKey, options);
                _logger.LogInformation("Added the Spelunkr guide to Live TV");
                added = true;
            }

            if (added)
            {
                _ = QueueGuideRefreshAsync(_taskManager, _logger);
            }
        }
        catch (Exception ex)
        {
            // Never take Jellyfin's startup down with us.
            _logger.LogError(ex, "Could not add Spelunkr to Live TV");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Asks Jellyfin to rebuild the TV guide now.</summary>
    /// <remarks>At startup scheduled tasks are registered after plugins start, so this waits up to 3 minutes for it.</remarks>
    internal static async Task QueueGuideRefreshAsync(ITaskManager taskManager, ILogger logger)
    {
        for (var i = 0; i < 36; i++)
        {
            var refresh = taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == RefreshGuideTaskKey);
            if (refresh is not null)
            {
                taskManager.QueueScheduledTask(refresh.ScheduledTask, new TaskOptions());
                logger.LogInformation("Queued a guide refresh for Spelunkr");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        logger.LogInformation("Guide refresh task not found; Spelunkr's guide fills in at the next scheduled refresh");
    }
}
