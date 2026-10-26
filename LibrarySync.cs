using System.Net.Http.Headers;
using System.Text.Json.Nodes;
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

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    // ---------- Очистка названий торрентов для Jellyfin ----------

    private static readonly Regex InfoHashRx = new(@"\b[0-9a-fA-F]{40}\b", RegexOptions.Compiled);
    private static readonly Regex LeadingTagsRx = new(@"^\s*(?:\[[^\]]*\]\s*)+", RegexOptions.Compiled);
    private static readonly Regex YearRx = new(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex SeasonRx = new(@"\b(?:S\d{1,2}(?:E\d+)?|\d{1,2}\s*-?\s*(?:й\s+)?сезон\w*|сезон\w*|season)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingRangeRx = new(@"\s+\d{1,2}\s*[-–—]\s*$", RegexOptions.Compiled);
    private static readonly Regex SeEpRx = new(@"(?<![A-Za-z0-9])[sS](\d{1,2})[ ._-]?[eE](\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex XEpRx = new(@"(?<!\d)(\d{1,2})[xхXХ](\d{2,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex EpOnlyRx = new(@"(?<![A-Za-z0-9])[eE](?:p(?:isode)?)?[ ._-]?(\d{1,3})(?!\d)|(\d{1,3})\s*[-_.]?\s*(?:серия|сер\.?|series)|(?:серия|эпизод)\s*[-_.]?\s*(\d{1,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonTextRx = new(@"(?:сезон|season)\s*[-_.:]?\s*(\d{1,2})|(?<![A-Za-z0-9])[sS](\d{1,2})(?![\dEeXx])|(\d{1,2})\s*-?\s*(?:й\s+)?сезон", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GenericEpisodeNameRx = new(@"^(?:Episode|Эпизод|Серия)\s*\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BracketsRx = new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
    private static readonly Regex CyrRx = new(@"[А-Яа-яЁё]", RegexOptions.Compiled);
    private static readonly Regex LatRx = new(@"[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex LetterRx = new(@"[^\W\d_]", RegexOptions.Compiled);
    private static readonly Regex LatWordRx = new(@"(?<![\p{L}\d])[A-Za-z][A-Za-z0-9'’.\-]*", RegexOptions.Compiled);
    private static readonly Regex EpisodeRx = new(@"[sS]\d{1,2}[eE]\d{1,3}|(?<!\d)\d{1,2}x\d{2,3}(?!\d)|[сС]ери[яи]", RegexOptions.Compiled);
    private static readonly Regex PartSplitRx = new(@"\s+/\s+|\s+\|\s+", RegexOptions.Compiled);

    private static readonly char[] EdgeChars =
    {
        ' ', '\t', '(', ')', '[', ']', '{', '}', '-', '–', '—', '/', '|', '.', ',', ':', ';', '«', '»', '"', '\'', '`'
    };

    /// <summary>
    /// "[NUM] Проект «Конец света» Project Hail Mary (IMAX) 2026 ДБ, ... bf313d6e..." -> "Проект Конец света", year "2026".
    /// </summary>
    private static string CleanTitle(string raw, out string? year)
    {
        var s = InfoHashRx.Replace(raw, " ");
        s = LeadingTagsRx.Replace(s, string.Empty);

        year = null;
        var head = s;
        foreach (Match m in YearRx.Matches(s))
        {
            if (LetterRx.IsMatch(s[..m.Index]))
            {
                year = m.Groups[1].Value;
                head = s[..m.Index];
                break;
            }
        }

        head = SeasonRx.Split(head)[0];
        head = TrailingRangeRx.Replace(head, string.Empty);
        head = BracketsRx.Replace(head, " ").Trim(EdgeChars);

        var parts = PartSplitRx.Split(head)
            .Select(p => p.Trim(EdgeChars))
            .Where(p => p.Length > 0)
            .ToList();
        var part = parts.FirstOrDefault(p => CyrRx.IsMatch(p))
            ?? parts.FirstOrDefault(p => LatRx.IsMatch(p))
            ?? parts.FirstOrDefault()
            ?? head;

        if (CyrRx.IsMatch(part))
        {
            var ru = part[CyrRx.Match(part).Index..];
            foreach (Match w in LatWordRx.Matches(ru))
            {
                var v = w.Value.TrimEnd('.', '-');
                if (v.Length < 2 || Regex.IsMatch(v, @"^[IVX]{1,4}$"))
                {
                    continue;
                }

                ru = ru[..w.Index];
                break;
            }

            part = ru;
        }

        part = Regex.Replace(part, "[«»“”„\"`]", " ");

        if (!part.Trim().Contains(' '))
        {
            part = Regex.Replace(part, @"[._]+", " ");
        }

        var title = Regex.Replace(part, @"\s+", " ").Trim(EdgeChars);
        return title.Length > 0 ? title : Regex.Replace(raw, @"\s+", " ").Trim();
    }

    private static readonly Regex FallbackYearRx = new(@"(?<![\dxXхХ×])(19[2-9]\d|20[0-3]\d)(?![\dxXхХ×])", RegexOptions.Compiled);

    private static string? FindYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string? year = null;
        foreach (Match m in FallbackYearRx.Matches(InfoHashRx.Replace(text, " ")))
        {
            if (int.Parse(m.Groups[1].Value) <= DateTime.UtcNow.Year + 1)
            {
                year = m.Groups[1].Value;
            }
        }

        return year;
    }

    private static string BuildName(string raw, IEnumerable<string?> yearSources)
    {
        var title = CleanTitle(raw, out var year);
        if (year == null)
        {
            foreach (var src in yearSources)
            {
                year = FindYear(src);
                if (year != null)
                {
                    break;
                }
            }
        }

        var name = year != null ? $"{title} ({year})" : title;
        return Sanitize(name.Replace(":", " -"));
    }

    // ---------- Разбор серий / сезонов ----------

    private static string FileNameOf(string path)
    {
        var i = path.LastIndexOfAny(new[] { '/', '\\' });
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static string FolderOf(string path)
    {
        var i = path.LastIndexOfAny(new[] { '/', '\\' });
        return i > 0 ? path[..i] : string.Empty;
    }

    private static int? FirstNumber(Match m)
    {
        for (var i = 1; i < m.Groups.Count; i++)
        {
            if (m.Groups[i].Success && m.Groups[i].Length > 0)
            {
                return int.Parse(m.Groups[i].Value);
            }
        }

        return null;
    }

    private static int? SeasonFromText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var m = SeasonTextRx.Match(text);
        return m.Success ? FirstNumber(m) : null;
    }

    private static (int Season, int? Episode) ParseEpisode(string path, int defaultSeason)
    {
        var name = FileNameOf(path);
        var m = SeEpRx.Match(name);
        if (!m.Success)
        {
            m = SeEpRx.Match(path);
        }

        if (!m.Success)
        {
            m = XEpRx.Match(name);
        }

        if (m.Success)
        {
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }

        var season = SeasonFromText(FolderOf(path)) ?? defaultSeason;
        var e = EpOnlyRx.Match(name);
        if (e.Success)
        {
            return (season, FirstNumber(e));
        }

        var lead = Regex.Match(name, @"^\s*(\d{1,3})(?!\d)");
        return (season, lead.Success ? int.Parse(lead.Groups[1].Value) : null);
    }

    // ---------- TMDB (год выхода сериала и названия серий) ----------

    private const string TmdbFile = ".tmdb.json";

    private sealed record ShowInfo(int Id, string Name, string? Year);

    private async Task<JsonNode?> TmdbGetAsync(string pathAndQuery, CancellationToken ct)
    {
        var key = Plugin.Instance?.Configuration.TmdbApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        try
        {
            var url = "https://api.themoviedb.org/3/" + pathAndQuery;
            var bearer = key.StartsWith("eyJ", StringComparison.Ordinal);
            if (!bearer)
            {
                url += (pathAndQuery.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key);
            }

            using var c = _http.CreateClient();
            c.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearer)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            using var resp = await c.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("TorrServer: TMDB ответил {Code} на {Path}", (int)resp.StatusCode, pathAndQuery);
                return null;
            }

            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning("TorrServer: TMDB недоступен: {Message}", e.Message);
            return null;
        }
    }

    private async Task<ShowInfo?> LookupShowAsync(string? existingDir, string query, CancellationToken ct)
    {
        if (existingDir != null)
        {
            var cache = Path.Combine(existingDir, TmdbFile);
            if (File.Exists(cache))
            {
                try
                {
                    var n = JsonNode.Parse(File.ReadAllText(cache));
                    if (n?["id"] != null)
                    {
                        return new ShowInfo((int)n["id"]!, (string?)n["name"] ?? query, (string?)n["year"]);
                    }
                }
                catch (Exception e)
                {
                    _log.LogDebug(e, "TorrServer: повреждён кэш {File}", cache);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.TmdbApiKey))
        {
            return null;
        }

        var res = await TmdbGetAsync("search/tv?language=ru-RU&query=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        var results = res?["results"]?.AsArray();
        if (results == null || results.Count == 0)
        {
            return null;
        }

        var pick = results.FirstOrDefault(r =>
                string.Equals((string?)r?["name"], query, StringComparison.OrdinalIgnoreCase)
                || string.Equals((string?)r?["original_name"], query, StringComparison.OrdinalIgnoreCase))
            ?? results[0];
        if (pick?["id"] == null)
        {
            return null;
        }

        var date = (string?)pick["first_air_date"];
        var year = !string.IsNullOrEmpty(date) && date.Length >= 4 ? date[..4] : null;
        var name = (string?)pick["name"];
        return new ShowInfo((int)pick["id"]!, string.IsNullOrWhiteSpace(name) ? query : name!, year);
    }

    private static void SaveShowCache(string dir, ShowInfo info)
    {
        var f = Path.Combine(dir, TmdbFile);
        if (!File.Exists(f))
        {
            var o = new JsonObject { ["id"] = info.Id, ["name"] = info.Name, ["year"] = info.Year };
            File.WriteAllText(f, o.ToJsonString());
        }
    }

    private async Task<Dictionary<int, string>> GetEpisodeNamesAsync(string dir, int showId, int season, int[] needed, CancellationToken ct)
    {
        var map = new Dictionary<int, string>();
        var file = Path.Combine(dir, $".tmdb-s{season:00}.json");
        var refresh = true;
        if (File.Exists(file))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject cached)
                {
                    foreach (var kv in cached)
                    {
                        if (int.TryParse(kv.Key, out var ep) && !string.IsNullOrWhiteSpace((string?)kv.Value))
                        {
                            map[ep] = (string)kv.Value!;
                        }
                    }
                }

                refresh = needed.Any(e => !map.ContainsKey(e))
                    && (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalHours > 12;
            }
            catch (Exception e)
            {
                _log.LogDebug(e, "TorrServer: повреждён кэш {File}", file);
            }
        }

        if (!refresh)
        {
            return map;
        }

        var res = await TmdbGetAsync($"tv/{showId}/season/{season}?language=ru-RU", ct).ConfigureAwait(false);
        var eps = res?["episodes"]?.AsArray();
        if (eps == null)
        {
            return map;
        }

        map.Clear();
        foreach (var ep in eps)
        {
            var num = (int?)ep?["episode_number"];
            var nm = ((string?)ep?["name"])?.Trim();
            if (num != null && !string.IsNullOrWhiteSpace(nm) && !GenericEpisodeNameRx.IsMatch(nm))
            {
                map[num.Value] = nm;
            }
        }

        var o = new JsonObject();
        foreach (var kv in map)
        {
            o[kv.Key.ToString()] = kv.Value;
        }

        File.WriteAllText(file, o.ToJsonString());
        return map;
    }

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path).Trim() == content)
        {
            return false;
        }

        File.WriteAllText(path, content);
        return true;
    }

    private static string Sanitize(string s)
    {
        s = Regex.Replace(s, @"[<>:""/\\|?*\x00-\x1F]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd(' ', '.');

        if (s.Length == 0)
        {
            s = "torrent";
        }

        if (ReservedWindowsNames.Contains(s))
        {
            s = "_" + s;
        }

        if (s.Length > 150)
        {
            s = s[..150].Trim().TrimEnd(' ', '.');
        }

        return s.Length == 0 ? "torrent" : s;
    }

    private static string NextAvailableDirectory(string parent, string name, string? current = null)
    {
        var candidate = Path.Combine(parent, name);
        if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(candidate))
        {
            return candidate;
        }

        for (var n = 2; ; n++)
        {
            candidate = Path.Combine(parent, $"{name} ({n})");
            if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
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
            return;
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

        var isShow = videos.Count > 1
            && (videos.Count(v => EpisodeRx.IsMatch(Path.GetFileName(v.Path))) >= 2
                || SeasonRx.IsMatch(t.DisplayName)
                || SeasonRx.IsMatch(t.Name ?? string.Empty));
        if (!isShow && videos.Count > 1)
        {
            videos = new List<TsFile> { videos.OrderByDescending(f => f.Length).First() };
        }

        var parent = isShow ? ShowsDir : MoviesDir;
        var existing = FindDir(t.Hash!);
        var yearSources = new[] { t.Name, videos[0].Path, string.Join(" ", files.Select(f => f.Path)) };

        string name;
        string showTitle = string.Empty;
        ShowInfo? info = null;
        if (isShow)
        {
            var title = CleanTitle(t.DisplayName, out var titleYear);
            info = await LookupShowAsync(existing, title, ct).ConfigureAwait(false);
            var shownTitle = info?.Name ?? title;
            var year = info?.Year ?? titleYear ?? yearSources.Select(FindYear).FirstOrDefault(y => y != null);
            showTitle = Sanitize(shownTitle.Replace(":", " -"));
            name = Sanitize((year != null ? $"{shownTitle} ({year})" : shownTitle).Replace(":", " -"));
        }
        else
        {
            name = BuildName(t.DisplayName, yearSources);
        }

        var dir = existing;
        if (dir == null)
        {
            dir = NextAvailableDirectory(parent, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Marker), t.Hash);
            changes++;
        }
        else
        {
            var desired = NextAvailableDirectory(parent, name, dir);
            if (!string.Equals(dir, desired, StringComparison.Ordinal))
            {
                Directory.CreateDirectory(parent);
                Directory.Move(dir, desired);
                _log.LogInformation("TorrServer: переименована папка {Old} -> {New}", dir, desired);
                dir = desired;
                changes++;
            }
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (isShow)
        {
            if (info != null)
            {
                SaveShowCache(dir, info);
            }

            var defaultSeason = SeasonFromText(t.DisplayName)
                ?? SeasonFromText(t.Name)
                ?? SeasonFromText(FolderOf(videos[0].Path))
                ?? 1;

            var items = new List<(TsFile File, int Season, int Episode)>();
            var counters = new Dictionary<int, int>();
            foreach (var v in videos)
            {
                var (season, ep) = ParseEpisode(v.Path, defaultSeason);
                if (ep == null)
                {
                    counters[season] = counters.GetValueOrDefault(season) + 1;
                    ep = counters[season];
                }

                items.Add((v, season, ep.Value));
            }

            var names = new Dictionary<int, Dictionary<int, string>>();
            if (info != null)
            {
                foreach (var g in items.GroupBy(i => i.Season))
                {
                    names[g.Key] = await GetEpisodeNamesAsync(dir, info.Id, g.Key, g.Select(i => i.Episode).ToArray(), ct)
                        .ConfigureAwait(false);
                }
            }

            foreach (var it in items.OrderBy(i => i.Season).ThenBy(i => i.Episode))
            {
                // Jellyfin распознаёт стандартную папку сезона "Season 01".
                var seasonDir = Path.Combine(dir, $"Season {it.Season:00}");
                Directory.CreateDirectory(seasonDir);

                // Формат: Название - S01E01 - Название серии.strm
                var baseName = $"{showTitle} - S{it.Season:00}E{it.Episode:00}";
                if (names.TryGetValue(it.Season, out var map) && map.TryGetValue(it.Episode, out var epName))
                {
                    var cleanEpisodeName = Sanitize(epName.Replace("[", " ").Replace("]", " ").Replace(":", " -"));
                    if (!string.IsNullOrWhiteSpace(cleanEpisodeName))
                    {
                        baseName += $" - {cleanEpisodeName}";
                    }
                }

                var strm = UniquePath(seasonDir, Sanitize(baseName), ".strm", taken);
                taken.Add(strm);
                var url = _client.StreamUrl(t.Hash!, it.File.Id, Path.GetFileName(it.File.Path), true);
                if (WriteIfChanged(strm, url))
                {
                    changes++;
                }
            }
        }
        else
        {
            var v = videos[0];
            var strm = UniquePath(dir, Path.GetFileName(dir), ".strm", taken);
            taken.Add(strm);
            var url = _client.StreamUrl(t.Hash!, v.Id, Path.GetFileName(v.Path), true);
            if (WriteIfChanged(strm, url))
            {
                changes++;
            }
        }

        // Старые S01/S02 и старые имена серий удалятся после создания новых файлов.
        foreach (var old in Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories))
        {
            if (!taken.Contains(old))
            {
                File.Delete(old);
                changes++;
            }
        }

        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (!Directory.EnumerateFileSystemEntries(sub).Any())
            {
                Directory.Delete(sub);
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
