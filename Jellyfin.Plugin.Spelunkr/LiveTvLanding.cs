// LiveTvLanding: makes Live TV open on the Guide for every account that hasn't picked a Live TV default screen,
// at startup and as soon as an account is created. Jellyfin has no server-wide default for this screen.
using Jellyfin.Data.Events.Users;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr;

/// <summary>Sets each account's Live TV default screen to the Guide when the account has none.</summary>
public sealed class LiveTvLanding : IHostedService, IEventConsumer<UserCreatedEventArgs>
{
    // Where jellyfin-web keeps the choice under Settings → Home → Live TV → Default screen: the "usersettings"
    // display preferences of client "emby". Jellyfin's DisplayPreferencesController turns a non-Guid preferences
    // id into an item id with GetMD5, so this is the same row the web client reads.
    internal const string Client = "emby";
    internal const string Key = "landing-livetv";
    internal const string Guide = "guide";
    internal static readonly Guid UserSettingsItemId = "usersettings".GetMD5();

    private readonly IUserManager _userManager;
    private readonly IDisplayPreferencesManager _prefs;
    private readonly ILogger<LiveTvLanding> _logger;

    /// <summary>Initializes a new instance of the <see cref="LiveTvLanding"/> class.</summary>
    public LiveTvLanding(IUserManager userManager, IDisplayPreferencesManager prefs, ILogger<LiveTvLanding> logger)
    {
        _userManager = userManager;
        _prefs = prefs;
        _logger = logger;
    }

    // Read on every use, not once, so the switch applies to accounts created later without a restart.
    // No loaded plugin counts as off: "off" must touch nobody.
    private static bool Enabled => Plugin.Instance?.Configuration.OpenLiveTvOnGuide ?? false;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Apply(Enabled, _userManager.GetUsersIds(), _prefs, _logger);
        }
        catch (Exception ex)
        {
            // Never take Jellyfin's startup down with us.
            _logger.LogWarning(ex, "Could not set Live TV to open on the guide");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        Apply(Enabled, [eventArgs.Argument.Id], _prefs, _logger);
        return Task.CompletedTask;
    }

    /// <summary>
    /// For each user with no Live TV default screen, stores the Guide. Any existing value, Programs included, is
    /// left alone. One user's failure is logged and the rest carry on.
    /// </summary>
    internal static void Apply(bool enabled, IEnumerable<Guid> userIds, IDisplayPreferencesManager prefs, ILogger logger)
    {
        if (!enabled)
        {
            return;
        }

        foreach (var userId in userIds)
        {
            try
            {
                var custom = prefs.ListCustomItemDisplayPreferences(userId, UserSettingsItemId, Client);
                if (custom.ContainsKey(Key))
                {
                    continue;
                }

                // Set replaces the user's whole set for this item and client, so the other preferences go back too.
                custom[Key] = Guide;
                prefs.SetCustomItemDisplayPreferences(userId, UserSettingsItemId, Client, custom);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not set Live TV to open on the guide for user {UserId}", userId);
            }
        }
    }
}
