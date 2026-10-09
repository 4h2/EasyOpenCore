using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyOpenCore.Core.Efi;

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>
/// Downloads GitHub releases and raw files with an on-disk cache
/// (%LocalAppData%\EasyOpenCore\cache). Release metadata is cached for a few hours because
/// unauthenticated API calls are limited to 60 per hour; set GITHUB_TOKEN to raise the limit.
/// </summary>
public sealed class GitHubClient : IDisposable
{
    private static readonly TimeSpan MetadataTtl = TimeSpan.FromHours(6);

    private readonly HttpClient _http;

    public GitHubClient(string? cacheRoot = null)
    {
        CacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyOpenCore", "cache");
        Directory.CreateDirectory(CacheRoot);

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EasyOpenCore", "1.0"));
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public string CacheRoot { get; }

    public async Task<GitHubRelease> GetLatestReleaseAsync(string repo, CancellationToken ct = default)
    {
        var file = Path.Combine(CacheRoot, "api", repo.Replace('/', '_') + ".json");
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MetadataTtl)
            return Deserialize(await File.ReadAllTextAsync(file, ct));

        try
        {
            var json = await _http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, json, ct);
            return Deserialize(json);
        }
        catch (HttpRequestException) when (File.Exists(file))
        {
            // Offline or rate-limited: an older cached answer is better than failing the build.
            return Deserialize(await File.ReadAllTextAsync(file, ct));
        }
    }

    private static GitHubRelease Deserialize(string json) =>
        JsonSerializer.Deserialize<GitHubRelease>(json) ?? throw new InvalidDataException("Invalid GitHub release JSON");

    /// <summary>Downloads a URL once; later calls return the cached file.</summary>
    public async Task<string> DownloadAsync(string url, string cacheSubfolder, CancellationToken ct = default)
    {
        var name = Path.GetFileName(new Uri(url).LocalPath);
        var target = Path.Combine(CacheRoot, "downloads", cacheSubfolder, name);
        if (File.Exists(target))
            return target;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".part";
        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(temp);
            await response.Content.CopyToAsync(file, ct);
        }
        File.Move(temp, target, overwrite: true);
        return target;
    }

    /// <summary>Downloads a text file without caching it (always fetches the latest version).</summary>
    public Task<string> GetStringAsync(string url, CancellationToken ct = default) => _http.GetStringAsync(url, ct);

    /// <summary>Extracts a zip next to its cached copy (once) and returns the folder.</summary>
    public string Extract(string zipPath)
    {
        var dir = Path.Combine(Path.GetDirectoryName(zipPath)!, Path.GetFileNameWithoutExtension(zipPath) + ".extracted");
        if (Directory.Exists(dir))
            return dir;

        var temp = dir + ".part";
        if (Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, temp);
        Directory.Move(temp, dir);
        return dir;
    }

    public void Dispose() => _http.Dispose();
}
