// Plugin: the plugin's identity for Jellyfin (name, id). It has no settings; everything is automatic.
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Spelunkr;

/// <summary>Spelunkr for Jellyfin.</summary>
/// <remarks>
/// Derives from the configuration-carrying base even with no settings: only that base's constructor records the
/// plugin's version and paths, and Jellyfin 12.3 fails to load a plugin without them.
/// </remarks>
public sealed class Plugin : BasePlugin<BasePluginConfiguration>
{
    /// <summary>The plugin id; also used in manifest.json. Never change it: Jellyfin tracks installs by it.</summary>
    public static readonly Guid PluginId = Guid.Parse("5f0d6b2e-7c1a-4b9e-9a43-3e2b8d4c1a07");

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => "Spelunkr";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Spelunkr.tv channels in Live TV, with a guide.";
}
