using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace KanTuNext.Desktop.Services.Updates;

/// <summary>
/// Looks for a newer release on GitHub and downloads its package for this platform. The only network access the
/// app makes; off when the user turns automatic checks off and never presses 检查更新.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    public const string ReleasesPage = "https://github.com/pengpercy/KanTuNext/releases";
    private const string ReleasesApi = "https://api.github.com/repos/pengpercy/KanTuNext/releases";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public UpdateService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "KanTuNext");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    /// <summary>The running version, from the VERSION file via the assembly.</summary>
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>The newest published, non-prerelease release if it is newer than this build; null if up to date.</summary>
    public async Task<ReleaseInfo?> FindNewerAsync(CancellationToken cancel = default)
    {
        var releases = await _http.GetFromJsonAsync(ReleasesApi, UpdateJson.Default.ListReleaseInfo, cancel) ?? [];
        var newest = releases
            .Where(r => r is { Draft: false, Prerelease: false } && r.Version is not null)
            .MaxBy(r => r.Version);
        return newest?.Version > CurrentVersion ? newest : null;
    }

    /// <summary>
    /// Streams the asset to <paramref name="destination"/>, reporting (bytes so far, total or -1), and checks it
    /// against the SHA-256 digest GitHub publishes for release assets.
    /// </summary>
    public async Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<(long Received, long Total)> progress,
        CancellationToken cancel)
    {
        using var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : -1);

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancel))
        await using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                sha256.AppendData(buffer, 0, read);
                received += read;
                progress.Report((received, total));
            }
        }

        const string prefix = "sha256:";
        if (asset.Digest?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            && !Convert.ToHexStringLower(sha256.GetHashAndReset()).Equals(asset.Digest[prefix.Length..], StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new InvalidDataException("下载的安装包校验失败。");
        }
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<ReleaseAsset> Assets { get; set; } = [];

    [JsonIgnore]
    public Version? Version => System.Version.TryParse(TagName.TrimStart('v'), out var v) ? v : null;

    [JsonIgnore]
    public string VersionText => TagName.TrimStart('v');
}

internal sealed class ReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>"sha256:&lt;hex&gt;", published by GitHub for release assets.</summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

[JsonSerializable(typeof(List<ReleaseInfo>))]
internal sealed partial class UpdateJson : JsonSerializerContext;
