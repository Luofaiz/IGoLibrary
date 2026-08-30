using IGoLibrary.Application.Abstractions;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Services;

public sealed class SettingsService(ISettingsRepository settingsRepository) : ISettingsService
{
    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        return settingsRepository.LoadAsync(cancellationToken);
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        return settingsRepository.SaveAsync(settings, cancellationToken);
    }
}
