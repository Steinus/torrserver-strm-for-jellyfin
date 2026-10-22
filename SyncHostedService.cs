using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

public class SyncHostedService : BackgroundService
{
    private readonly LibrarySync _sync;
    private readonly ILogger<SyncHostedService> _log;

    public SyncHostedService(LibrarySync sync, ILogger<SyncHostedService> log)
    {
        _sync = sync;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = Plugin.Instance?.Configuration.SyncIntervalMinutes ?? 30;
            if (minutes > 0)
            {
                try
                {
                    await _sync.RunAsync(null, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogWarning("TorrServer: синхронизация не удалась: {Message}", e.Message);
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(minutes > 0 ? minutes : 1), stoppingToken).ConfigureAwait(false);
        }
    }
}