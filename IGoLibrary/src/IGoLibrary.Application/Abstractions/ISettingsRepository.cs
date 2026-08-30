using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Abstractions;

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
