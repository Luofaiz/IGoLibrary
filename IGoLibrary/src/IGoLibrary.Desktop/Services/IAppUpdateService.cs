namespace IGoLibrary.Desktop.Services;

public interface IAppUpdateService
{
    string CurrentVersionText { get; }

    Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    Task<AppUpdateInstallResult> InstallUpdateAsync(AppUpdateCheckResult update, CancellationToken cancellationToken = default);
}

public sealed record AppUpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? Notes,
    string? DownloadUrl,
    string? DownloadSha256,
    string? ReleaseUrl);

public sealed record AppUpdateInstallResult(string InstallerPath);
