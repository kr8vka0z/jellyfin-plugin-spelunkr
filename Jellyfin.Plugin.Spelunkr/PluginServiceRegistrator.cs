// PluginServiceRegistrator: hands the plugin's tuner, guide source and startup registration to Jellyfin.
using Jellyfin.Plugin.Spelunkr.Guide;
using MediaBrowser.Controller;
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
        serviceCollection.AddSingleton<ITunerHost, SpelunkrTunerHost>();
        serviceCollection.AddSingleton<IListingsProvider, SpelunkrGuide>();
        serviceCollection.AddHostedService<Registration>();
    }
}
