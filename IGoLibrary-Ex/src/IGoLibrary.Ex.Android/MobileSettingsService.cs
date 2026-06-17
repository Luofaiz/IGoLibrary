using IGoLibrary.Ex.Application.Abstractions;
using IGoLibrary.Ex.Domain.Models;

namespace IGoLibrary.Ex.Android;

internal sealed class MobileSettingsService : ISettingsService
{
    private AppSettings _settings = AppSettings.Default with
    {
        ApiTimeoutSeconds = 10,
        RetryCount = 2
    };

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_settings);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}
