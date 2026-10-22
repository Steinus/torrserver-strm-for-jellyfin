using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TorrServer;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Адрес TorrServer, например http://192.168.1.50:8090</summary>
    public string TorrServerUrl { get; set; } = "http://127.0.0.1:8090";

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Папка, куда плагин кладёт .strm файлы. Пусто = data/torrserver внутри Jellyfin.</summary>
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>Как часто синхронизировать список с TorrServer (минуты). 0 = выключено.</summary>
    public int SyncIntervalMinutes { get; set; } = 30;

    /// <summary>Запускать сканирование библиотек Jellyfin после изменений.</summary>
    public bool AutoScanLibrary { get; set; } = true;
}