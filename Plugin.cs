using System.Globalization;
using Jellyfin.Plugin.LibraryDoctor.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LibraryDoctor;

/// <summary>Library Doctor: a weekly checkup of the library that lists files and metadata that need attention.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "9dff327a-0a86-41c0-b5b9-26ecede62164";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Library Doctor";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "A checkup for your library: unreadable files, missing episodes, duplicates, bad matches, missing artwork, and files that will need converting.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Library Doctor",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "healing",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        };
    }
}
