using Avalonia.Threading;

namespace IGoLibrary.Desktop.Services;

public sealed class ConfirmationDialogService(AppWindowService appWindowService) : IConfirmationDialogService
{
    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "确认",
        string cancelText = "取消",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Dispatcher.UIThread.CheckAccess())
        {
            return await ShowCoreAsync(title, message, confirmText, cancelText, cancellationToken);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var confirmed = await ShowCoreAsync(title, message, confirmText, cancelText, cancellationToken);
                completion.TrySetResult(confirmed);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return await completion.Task;
    }

    private async Task<bool> ShowCoreAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (appWindowService.MainWindow is not { } owner)
        {
            return false;
        }

        var dialog = new ConfirmationDialogWindow(title, message, confirmText, cancelText);
        return await dialog.ShowDialog<bool>(owner);
    }
}
