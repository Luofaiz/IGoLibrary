using Android.App;
using Android.Widget;
using IGoLibrary.Ex.Application.Abstractions;

namespace IGoLibrary.Ex.Android;

internal sealed class MobileNotificationService(Activity activity) : INotificationService
{
    public Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default)
        => ShowToastAsync(title, message);

    public Task ShowWarningAsync(string title, string message, CancellationToken cancellationToken = default)
        => ShowToastAsync(title, message);

    public Task ShowSuccessAsync(string title, string message, CancellationToken cancellationToken = default)
        => ShowToastAsync(title, message);

    private Task ShowToastAsync(string title, string message)
    {
        activity.RunOnUiThread(() =>
        {
            Toast.MakeText(activity, $"{title}：{message}", ToastLength.Long)?.Show();
        });
        return Task.CompletedTask;
    }
}
