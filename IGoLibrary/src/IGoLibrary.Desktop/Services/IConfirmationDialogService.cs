namespace IGoLibrary.Desktop.Services;

public interface IConfirmationDialogService
{
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "确认",
        string cancelText = "取消",
        CancellationToken cancellationToken = default);
}
