using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Зеркалит содержимое TorrServer в папки с .strm файлами, которые индексирует Jellyfin.</summary>
public class LibrarySync
{
    private const string Marker = ".torrserver";

    private static readonly string[] VideoExt =
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".m2ts", ".wmv", ".webm", ".mpg", ".mpeg"
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TorrServerClient _client;
    private readonly IHttpClientFactory _http;
    private readonly ILibraryManager _library;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<LibrarySync> _log;

    public LibrarySync(
        TorrServerClient client,
        IHttpClientFactory http,
        ILibraryManager library,
        IApplicationPaths paths,
        ILogger<LibrarySync> log)
    {
        _client = client;
        _http = http;
        _library = library;
        _paths = paths;
        _log = log;
    }

    public string Root
    {
        get
        {
            var p = Plugin.Instance?.Configuration.LibraryPath;
            return string.IsNullOrWhiteSpace(p) ? Path.Combine(_paths.DataPath, "torrserver") : p!;
        }
    }

    public string MoviesDir => Path.Combine(Root, "Movies");

    public string ShowsDir => Path.Combine(Root, "Shows");

    public static bool IsVideo(string path)
        => VideoExt.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string Sanitize(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(ch, ' ');
        }

        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
        return s.Length == 0 ? "torrent" : (s.Length > 150 ? s[..150].Trim() : s);
    }

    public string? FindDir(string hash)
    {
        foreach (var parent in new[] { MoviesDir, ShowsDir })
        {
            if (!Directory.Exists(parent))
            {
                continue;
            }

            foreach (var dir in Directory.GetDirectories(parent))
            {
                var m = Path.Combine(dir, Marker);
                if (File.Exists(m) && string.Equals(File.ReadAllText(m).Trim(), hash, StringComparison.OrdinalIgnoreCase))
                {
                    return dir;
                }
            }
        }

        return null;
    }

    private static string UniquePath(string dir, string name, string ext, HashSet<string> taken)
    {
        var p = Path.Combine(dir, name + ext);
        for (var i = 2; taken.Contains(p); i++)
        {
            p = Path.Combine(dir, $"{name} ({i}){ext}");
        }

        return p;
    }

    public async Task RunAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (!await _lock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return; // синхронизация уже идёт
        }

        try
        {
            Directory.CreateDirectory(MoviesDir);
            Directory.CreateDirectory(ShowsDir);

            var torrents = await _client.ListAsync(ct).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changes = 0;
            var i = 0;

            foreach (var t in torrents)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(100.0 * i++ / Math.Max(1, torrents.Count));
                if (string.IsNullOrEmpty(t.Hash))
                {
                    continue;
                }

                seen.Add(t.Hash);
                try
                {
                    changes += await SyncTorrentAsync(t, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogWarning(e, "TorrServer: не удалось синхронизировать {Name}", t.DisplayName);
                }
            }

            // удалить то, чего больше нет в TorrServer
            foreach (var parent in new[] { MoviesDir, ShowsDir })
            {
                foreach (var dir in Directory.GetDirectories(parent))
                {
                    var m = Path.Combine(dir, Marker);
                    if (File.Exists(m) && !seen.Contains(File.ReadAllText(m).Trim()))
                    {
                        Directory.Delete(dir, true);
                        changes++;
                    }
                }
            }

            progress?.Report(100);
            if (changes > 0 && (Plugin.Instance?.Configuration.AutoScanLibrary ?? true))
            {
                _library.QueueLibraryScan();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<int> SyncTorrentAsync(TsTorrent t, CancellationToken ct)
    {
        var files = t.Files;
        if (files == null || files.Count == 0)
        {
            _log.LogInformation("TorrServer: ожидаю метаданные файлов для {Name} ({Hash})", t.DisplayName, t.Hash);
            files = (await _client.GetWithFilesAsync(t.Hash!, ct).ConfigureAwait(false))?.Files;
        }

        if (files == null || files.Count == 0)
        {
            _log.LogWarning("TorrServer: file_stats не получены для {Name} ({Hash}), .strm не создан", t.DisplayName, t.Hash);
            return 0;
        }

        var videos = files.Where(f => IsVideo(f.Path))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (videos.Count == 0)
        {
            _log.LogWarning("TorrServer: в {Name} ({Hash}) нет поддерживаемых видеофайлов; получено файлов: {Count}", t.DisplayName, t.Hash, files.Count);
            return 0;
        }

        var changes = 0;
        var isShow = videos.Count > 1;
        var dir = FindDir(t.Hash!);
        if (dir == null)
        {
            var parent = isShow ? ShowsDir : MoviesDir;
            var name = Sanitize(t.DisplayName);
            dir = Path.Combine(parent, name);
            for (var n = 2; Directory.Exists(dir); n++)
            {
                dir = Path.Combine(parent, $"{name} ({n})");
            }

            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Marker), t.Hash);
            changes++;
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in videos)
        {
            var baseName = isShow ? Sanitize(Path.GetFileNameWithoutExtension(v.Path)) : Path.GetFileName(dir);
            var strm = UniquePath(dir, baseName, ".strm", taken);
            taken.Add(strm);

            var url = _client.StreamUrl(t.Hash!, v.Id, Path.GetFileName(v.Path), true);
            if (!File.Exists(strm) || File.ReadAllText(strm).Trim() != url)
            {
                File.WriteAllText(strm, url);
                changes++;
            }
        }

        foreach (var old in Directory.GetFiles(dir, "*.strm"))
        {
            if (!taken.Contains(old))
            {
                File.Delete(old);
                changes++;
            }
        }

        var posterPath = Path.Combine(dir, "poster.jpg");
        if (!File.Exists(posterPath)
            && Uri.TryCreate(t.Poster, UriKind.Absolute, out var pu)
            && (pu.Scheme == Uri.UriSchemeHttp || pu.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                using var c = _http.CreateClient();
                c.Timeout = TimeSpan.FromSeconds(20);
                File.WriteAllBytes(posterPath, await c.GetByteArrayAsync(pu, ct).ConfigureAwait(false));
                changes++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogDebug(e, "TorrServer: постер не скачался {Url}", pu);
            }
        }

        return changes;
    }
}