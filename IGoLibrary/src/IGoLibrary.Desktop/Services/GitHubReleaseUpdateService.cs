using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace IGoLibrary.Desktop.Services;

public sealed class GitHubReleaseUpdateService : IAppUpdateService, IDisposable
{
    private const string DefaultManifestUrl = "https://github.com/Luofaiz/IGoLibrary/releases/latest/download/latest.json";
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly string _manifestUrl;

    public GitHubReleaseUpdateService(IConfiguration configuration)
        : this(configuration, new HttpClient(), disposeHttpClient: true)
    {
    }

    internal GitHubReleaseUpdateService(IConfiguration configuration, HttpClient httpClient, bool disposeHttpClient = false)
    {
        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _manifestUrl = configuration["Updates:ManifestUrl"]?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_manifestUrl))
        {
            _manifestUrl = DefaultManifestUrl;
        }
    }

    public string CurrentVersionText => GetCurrentVersionText();

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(_manifestUrl, UriKind.Absolute, out var manifestUri) ||
            manifestUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("更新清单地址必须是有效的 http 或 https URL。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.UserAgent.ParseAdd("IGoLibrary-Updater/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(
            contentStream,
            UpdateJsonSerializerContext.Default.UpdateManifest,
            cancellationToken);
        if (manifest is null)
        {
            throw new InvalidOperationException("更新清单内容为空或格式不正确。");
        }

        var currentVersion = CurrentVersionText;
        var latestVersion = manifest.Version?.Trim();
        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            throw new InvalidOperationException("更新清单缺少 version 字段。");
        }

        var updateAvailable = CompareVersions(latestVersion, currentVersion) > 0;
        var downloadUrl = SelectDownloadUrl(manifest);
        ValidateOptionalWebUrl(downloadUrl, "下载地址");
        ValidateOptionalWebUrl(manifest.ReleaseUrl, "发布页地址");
        var downloadSha256 = string.IsNullOrWhiteSpace(manifest.DownloadSha256)
            ? null
            : manifest.DownloadSha256.Trim();

        return new AppUpdateCheckResult(
            updateAvailable,
            currentVersion,
            latestVersion,
            manifest.Notes,
            downloadUrl,
            downloadSha256,
            manifest.ReleaseUrl);
    }

    public async Task<AppUpdateInstallResult> InstallUpdateAsync(AppUpdateCheckResult update, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            throw new InvalidOperationException("更新清单没有提供安装程序下载地址。");
        }

        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("安装程序下载地址必须是有效的 http 或 https URL。");
        }

        var installerPath = BuildInstallerCachePath(update.LatestVersion);
        Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        request.Headers.UserAgent.ParseAdd("IGoLibrary-Updater/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        progress?.Report(new UpdateDownloadProgress(0, totalBytes));
        await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[64 * 1024];
            long downloaded = 0;
            int read;
            while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                progress?.Report(new UpdateDownloadProgress(downloaded, totalBytes));
            }
        }

        if (!string.IsNullOrWhiteSpace(update.DownloadSha256))
        {
            var actualSha256 = await CalculateSha256Async(installerPath, cancellationToken);
            if (!string.Equals(actualSha256, update.DownloadSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(installerPath);
                throw new InvalidOperationException("安装程序校验失败，请稍后重新下载。");
            }
        }

        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true
        });

        return new AppUpdateInstallResult(installerPath);
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static int CompareVersions(string left, string right)
    {
        var leftVersion = NormalizeVersion(left);
        var rightVersion = NormalizeVersion(right);
        return leftVersion.CompareTo(rightVersion);
    }

    private static Version NormalizeVersion(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var metadataIndex = normalized.IndexOfAny(['-', '+']);
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4)
        {
            throw new InvalidOperationException($"版本号格式不正确：{value}");
        }

        var numericParts = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numericParts[i]) || numericParts[i] < 0)
            {
                throw new InvalidOperationException($"版本号格式不正确：{value}");
            }
        }

        return new Version(numericParts[0], numericParts[1], numericParts[2], numericParts[3]);
    }

    private static string GetCurrentVersionText()
    {
        var version = typeof(GitHubReleaseUpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
        {
            version = typeof(GitHubReleaseUpdateService).Assembly.GetName().Version?.ToString();
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return "1.0.0";
        }

        var metadataIndex = version.IndexOf('+');
        return metadataIndex >= 0 ? version[..metadataIndex] : version;
    }

    private static string? SelectDownloadUrl(UpdateManifest manifest)
    {
        var url = manifest.DownloadUrls?
            .Select(static item => item?.Trim())
            .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item));

        return string.IsNullOrWhiteSpace(url)
            ? manifest.DownloadUrl?.Trim()
            : url;
    }

    private static string BuildInstallerCachePath(string? latestVersion)
    {
        var versionLabel = string.IsNullOrWhiteSpace(latestVersion)
            ? DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
            : latestVersion.Trim().TrimStart('v', 'V');
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            versionLabel = versionLabel.Replace(invalidChar, '-');
        }

        return Path.Combine(Path.GetTempPath(), "IGoLibrary", $"IGoLibrarySetup-{versionLabel}.exe");
    }

    private static async Task<string> CalculateSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ValidateOptionalWebUrl(string? url, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{fieldName}必须是有效的 http 或 https URL。");
        }
    }

    internal sealed class UpdateManifest
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("downloadUrls")]
        public string[]? DownloadUrls { get; set; }

        [JsonPropertyName("downloadSha256")]
        public string? DownloadSha256 { get; set; }

        [JsonPropertyName("releaseUrl")]
        public string? ReleaseUrl { get; set; }
    }
}

[JsonSerializable(typeof(GitHubReleaseUpdateService.UpdateManifest))]
internal partial class UpdateJsonSerializerContext : JsonSerializerContext;
