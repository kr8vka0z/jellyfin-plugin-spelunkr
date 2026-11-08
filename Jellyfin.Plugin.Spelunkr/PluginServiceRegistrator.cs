// PluginServiceRegistrator: hands the plugin's tuner, guide source and startup registration and Live TV landing setup to Jellyfin.
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Spelunkr.Guide;
using Jellyfin.Plugin.Spelunkr.Stream;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Spelunkr;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SpelunkrSource>();
        serviceCollection.AddSingleton<ArchiveOriginals>();
        serviceCollection.AddSingleton<ITunerHost, SpelunkrTunerHost>();
        serviceCollection.AddSingleton<IListingsProvider, SpelunkrGuide>();
        serviceCollection.AddHostedService<Registration>();
        serviceCollection.AddHostedService<LiveTvLanding>();
        serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, LiveTvLanding>();
    }
}
