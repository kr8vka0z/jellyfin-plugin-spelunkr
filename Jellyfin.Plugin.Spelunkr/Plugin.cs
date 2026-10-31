// Plugin: the plugin's identity for Jellyfin (name, id) and its one setting, with the dashboard page that changes it.
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Spelunkr;

/// <summary>The plugin's saved settings.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether newly opened streams play inside the TV cabinet.</summary>
    public bool ShowCabinet { get; set; } = true;
}

/// <summary>Spelunkr for Jellyfin.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id; also used in manifest.json. Never change it: Jellyfin tracks installs by it.</summary>
    public static readonly Guid PluginId = Guid.Parse("5f0d6b2e-7c1a-4b9e-9a43-3e2b8d4c1a07");

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin, null until Jellyfin has loaded it.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Spelunkr";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Spelunkr.tv channels in Live TV, with a guide.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
        [new PluginPageInfo { Name = Name, EmbeddedResourcePath = "configPage.html" }];
}
