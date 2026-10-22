using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Ручной запуск из Панели управления → Запланированные задачи.</summary>
public class SyncTask : IScheduledTask
{
    private readonly LibrarySync _sync;

    public SyncTask(LibrarySync sync)
    {
        _sync = sync;
    }

    public string Name => "Синхронизация с TorrServer";

    public string Key => "TorrServerSync";

    public string Description => "Обновляет .strm файлы по списку торрентов в TorrServer";

    public string Category => "TorrServer";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _sync.RunAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}