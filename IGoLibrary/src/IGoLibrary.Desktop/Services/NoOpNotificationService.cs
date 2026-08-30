using IGoLibrary.Application.Abstractions;

namespace IGoLibrary.Desktop.Services;

internal sealed class NoOpNotificationService : INotificationService
{
    public Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ShowWarningAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ShowSuccessAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
