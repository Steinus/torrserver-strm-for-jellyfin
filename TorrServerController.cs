using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrServer.Api;

[ApiController]
[Route("TorrServer")]
public class TorrServerController : ControllerBase
{
    private readonly TorrServerClient _client;
    private readonly LibrarySync _sync;
    private readonly ILibraryManager _library;

    public TorrServerController(TorrServerClient client, LibrarySync sync, ILibraryManager library)
    {
        _client = client;
        _sync = sync;
        _library = library;
    }

    [HttpGet("Test")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Test(CancellationToken ct)
    {
        try
        {
            return Ok(new { version = (await _client.EchoAsync(ct)).Trim() });
        }
        catch (Exception e)
        {
            return StatusCode(502, e.Message);
        }
    }

    [HttpGet("Torrents")]
    [Authorize]
    public async Task<ActionResult> List(CancellationToken ct)
    {
        try
        {
            var l = await _client.ListAsync(ct);
            return Ok(l.Where(t => !string.IsNullOrEmpty(t.Hash))
                .Select(t => new { hash = t.Hash, title = t.DisplayName, poster = t.Poster, size = t.Size }));
        }
        catch (Exception e)
        {
            return StatusCode(502, e.Message);
        }
    }

    [HttpGet("Torrents/{hash}")]
    [Authorize]
    public async Task<ActionResult> Files(string hash, CancellationToken ct)
    {
        try
        {
            var t = await _client.GetAsync(hash, ct);
            var files = (t?.Files ?? new List<TsFile>())
                .Where(f => LibrarySync.IsVideo(f.Path))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(f => new { id = f.Id, name = System.IO.Path.GetFileName(f.Path), length = f.Length });
            return Ok(new { files });
        }
        catch (Exception e)
        {
            return StatusCode(502, e.Message);
        }
    }

    [HttpPost("Add")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Add([FromBody] AddRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Link))
        {
            return BadRequest("Нужна magnet-ссылка или URL .torrent");
        }

        try
        {
            await _client.AddAsync(req.Link.Trim(), req.Title, req.Poster, ct);
            _ = Task.Run(() => _sync.RunAsync(null, CancellationToken.None));
            return NoContent();
        }
        catch (Exception e)
        {
            return StatusCode(502, e.Message);
        }
    }

    [HttpDelete("Torrents/{hash}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Remove(string hash, CancellationToken ct)
    {
        try
        {
            await _client.RemoveAsync(hash, ct);
            _ = Task.Run(() => _sync.RunAsync(null, CancellationToken.None));
            return NoContent();
        }
        catch (Exception e)
        {
            return StatusCode(502, e.Message);
        }
    }

    [HttpPost("Sync")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Sync(CancellationToken ct)
    {
        try
        {
            await _sync.RunAsync(null, ct);
            return NoContent();
        }
        catch (Exception e)
        {
            return StatusCode(502, e.Message);
        }
    }

    /// <summary>Находит элемент библиотеки Jellyfin, соответствующий торренту.</summary>
    [HttpGet("Resolve/{hash}")]
    [Authorize]
    public ActionResult Resolve(string hash)
    {
        var dir = _sync.FindDir(hash);
        if (dir == null)
        {
            return NotFound();
        }

        MediaBrowser.Controller.Entities.BaseItem? item;
        if (dir.StartsWith(_sync.MoviesDir, StringComparison.OrdinalIgnoreCase))
        {
            var strm = Directory.GetFiles(dir, "*.strm").FirstOrDefault();
            item = strm == null ? null : _library.FindByPath(strm, false);
        }
        else
        {
            item = _library.FindByPath(dir, true);
        }

        return item == null ? NotFound() : Ok(new { id = item.Id });
    }

    /// <summary>Прокси потока TorrServer → браузер (с Range), чтобы работало и по HTTPS, и из-за NAT.</summary>
    [HttpGet("Stream/{hash}/{index:int}")]
    [Authorize]
    public async Task Stream(string hash, int index, CancellationToken ct)
    {
        using var http = _client.CreateClient(true);
        using var req = new HttpRequestMessage(HttpMethod.Get, _client.StreamUrl(hash, index, "video", false));
        if (Request.Headers.TryGetValue("Range", out var range))
        {
            req.Headers.TryAddWithoutValidation("Range", range.ToString());
        }

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        Response.StatusCode = (int)resp.StatusCode;
        var h = resp.Content.Headers;
        if (h.ContentType != null)
        {
            Response.ContentType = h.ContentType.ToString();
        }

        if (h.ContentLength.HasValue)
        {
            Response.ContentLength = h.ContentLength;
        }

        if (h.ContentRange != null)
        {
            Response.Headers["Content-Range"] = h.ContentRange.ToString();
        }

        Response.Headers["Accept-Ranges"] = "bytes";
        await resp.Content.CopyToAsync(Response.Body, ct);
    }
}