using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TorrServer;

public class TsTorrent
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("poster")] public string? Poster { get; set; }
    [JsonPropertyName("hash")] public string? Hash { get; set; }
    [JsonPropertyName("torrent_size")] public long Size { get; set; }
    [JsonPropertyName("file_stats")] public List<TsFile>? Files { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Title) ? Title! : (Name ?? Hash ?? "torrent");
}

public class TsFile
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("length")] public long Length { get; set; }
}

public class AddRequest
{
    public string Link { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Poster { get; set; }
}