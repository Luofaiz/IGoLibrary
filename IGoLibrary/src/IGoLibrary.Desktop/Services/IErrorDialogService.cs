namespace IGoLibrary.Desktop.Services;

public interface IErrorDialogService
{
    Task ShowErrorAsync(string title, string errorType, string errorMessage, CancellationToken cancellationToken = default);
}
