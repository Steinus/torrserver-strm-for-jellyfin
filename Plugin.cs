using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TorrServer;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "TorrServer";

    public override Guid Id => Guid.Parse("b7a1c7d2-4f55-4e0b-9a52-6c1d3e8f2a10");

    public override string Description => "Просмотр торрентов из TorrServer прямо в Jellyfin";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            // Страница настроек (имя совпадает с именем плагина)
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{ns}.Web.config.html"
            },
            // Вкладка TorrServer в боковом меню
            new PluginPageInfo
            {
                Name = "TorrServerLibrary",
                DisplayName = "TorrServer",
                EmbeddedResourcePath = $"{ns}.Web.torrserver.html",
                EnableInMainMenu = true,
                MenuIcon = "cloud_download"
            }
        };
    }
}