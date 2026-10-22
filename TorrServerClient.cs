using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.TorrServer;

public class TorrServerClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly IHttpClientFactory _factory;

    public TorrServerClient(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    private static PluginConfiguration Cfg => Plugin.Instance!.Configuration;

    public Uri BaseUri
    {
        get
        {
            var s = Cfg.TorrServerUrl?.Trim() ?? string.Empty;
            if (s.Length == 0)
            {
                throw new InvalidOperationException("Адрес TorrServer не задан в настройках плагина");
            }

            if (!s.Contains("://", StringComparison.Ordinal))
            {
                s = "http://" + s;
            }

            return new Uri(s.TrimEnd('/') + "/");
        }
    }

    public HttpClient CreateClient(bool infiniteTimeout = false)
    {
        var c = _factory.CreateClient();
        c.BaseAddress = BaseUri;
        c.Timeout = infiniteTimeout ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(60);
        if (!string.IsNullOrEmpty(Cfg.Username))
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Cfg.Username}:{Cfg.Password}"));
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }

        return c;
    }

    private async Task<string> PostAsync(object body, CancellationToken ct)
    {
        using var c = CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var r = await c.PostAsync("torrents", content, ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> EchoAsync(CancellationToken ct)
    {
        using var c = CreateClient();
        return await c.GetStringAsync("echo", ct).ConfigureAwait(false);
    }

    public async Task<List<TsTorrent>> ListAsync(CancellationToken ct)
    {
        var s = await PostAsync(new { action = "list" }, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(s) || s.Trim() == "null")
        {
            return new List<TsTorrent>();
        }

        return JsonSerializer.Deserialize<List<TsTorrent>>(s, Json) ?? new List<TsTorrent>();
    }

    public async Task<TsTorrent?> GetAsync(string hash, CancellationToken ct)
    {
        var s = await PostAsync(new { action = "get", hash }, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TsTorrent>(s, Json);
    }

    public async Task<TsTorrent?> GetWithFilesAsync(string hash, CancellationToken ct)
    {
        TsTorrent? last = null;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            last = await GetAsync(hash, ct).ConfigureAwait(false);
            if (last?.Files is { Count: > 0 })
            {
                return last;
            }

            if (attempt < 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }

        return last;
    }

    public Task AddAsync(string link, string? title, string? poster, CancellationToken ct)
        => PostAsync(new { action = "add", link, title = title ?? string.Empty, poster = poster ?? string.Empty, save_to_db = true }, ct);

    public Task RemoveAsync(string hash, CancellationToken ct)
        => PostAsync(new { action = "rem", hash }, ct);

    /// <summary>Ссылка на поток. Для .strm (читает сам сервер Jellyfin) можно вшить логин/пароль.</summary>
    public string StreamUrl(string hash, int index, string fileName, bool withCredentials)
    {
        var u = BaseUri;
        var auth = string.Empty;
        if (withCredentials && !string.IsNullOrEmpty(Cfg.Username))
        {
            auth = $"{Uri.EscapeDataString(Cfg.Username)}:{Uri.EscapeDataString(Cfg.Password)}@";
        }

        var prefix = u.AbsolutePath.TrimEnd('/');
        return $"{u.Scheme}://{auth}{u.Authority}{prefix}/stream/{Uri.EscapeDataString(fileName)}?link={hash}&index={index}&play";
    }
}