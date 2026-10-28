using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TorrServer;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private const string StableConfigurationFileName = "Jellyfin.Plugin.TorrServer.xml";
    private const string BackupXmlFileName = "torrserver-strm-settings.xml";
    private const string BackupJsonFileName = "torrserver-strm-settings.json";

    private readonly string _backupXmlPath;
    private readonly string _backupJsonPath;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        _backupXmlPath = Path.Combine(applicationPaths.ProgramDataPath, BackupXmlFileName);
        _backupJsonPath = Path.Combine(applicationPaths.ProgramDataPath, BackupJsonFileName);

        RestoreConfigurationIfNeeded(xmlSerializer);
        EnsureBackups(xmlSerializer);

        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "TorrServer";

    public override Guid Id => Guid.Parse("b7a1c7d2-4f55-4e0b-9a52-6c1d3e8f2a10");

    public override string Description => "Просмотр торрентов из TorrServer прямо в Jellyfin";

    // Закрепляем имя XML-конфига явно, чтобы обновления/переименования сборки
    // никогда не меняли путь к настройкам.
    public override string ConfigurationFileName => StableConfigurationFileName;

    public override void SaveConfiguration(PluginConfiguration config)
    {
        base.SaveConfiguration(config);
        SaveBackups(config);
    }

    private void RestoreConfigurationIfNeeded(IXmlSerializer xmlSerializer)
    {
        if (TryReadXml(xmlSerializer, ConfigurationFilePath, out _))
        {
            return;
        }

        // Сначала пробуем независимую XML-копию: она максимально совместима
        // со штатным сериализатором Jellyfin.
        if (TryReadXml(xmlSerializer, _backupXmlPath, out var xmlBackup) && xmlBackup is not null)
        {
            Configuration = xmlBackup;
            base.SaveConfiguration(xmlBackup);
            SaveBackups(xmlBackup);
            return;
        }

        // JSON — второй резервный формат на случай повреждения/удаления XML.
        if (TryReadJson(_backupJsonPath, out var jsonBackup) && jsonBackup is not null)
        {
            Configuration = jsonBackup;
            base.SaveConfiguration(jsonBackup);
            SaveBackups(jsonBackup);
        }
    }

    private void EnsureBackups(IXmlSerializer xmlSerializer)
    {
        if (!TryReadXml(xmlSerializer, ConfigurationFilePath, out var config) || config is null)
        {
            return;
        }

        SaveBackups(config);
    }

    private void SaveBackups(PluginConfiguration config)
    {
        try
        {
            var dir = Path.GetDirectoryName(_backupXmlPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // XML-копия через тот же сериализатор, что использует Jellyfin.
            XmlSerializer.SerializeToFile(config, _backupXmlPath);

            // Дополнительная человекочитаемая JSON-копия.
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            WriteTextAtomic(_backupJsonPath, json);
        }
        catch
        {
            // Ошибка резервной копии не должна мешать работе самого плагина.
        }
    }

    private static bool TryReadXml(IXmlSerializer xmlSerializer, string path, out PluginConfiguration? config)
    {
        config = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            config = xmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), path) as PluginConfiguration;
            return config is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadJson(string path, out PluginConfiguration? config)
    {
        config = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            config = JsonSerializer.Deserialize<PluginConfiguration>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return config is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteTextAtomic(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, true);
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{ns}.Web.config.html"
            },
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
