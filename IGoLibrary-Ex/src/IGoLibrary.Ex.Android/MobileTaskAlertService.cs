using IGoLibrary.Ex.Application.Abstractions;
using IGoLibrary.Ex.Domain.Models;

namespace IGoLibrary.Ex.Android;

internal sealed class MobileTaskAlertService(INotificationService notificationService) : ITaskAlertService
{
    public Task NotifyCookieExpiredAsync(string source, string reason, CancellationToken cancellationToken = default)
        => notificationService.ShowWarningAsync("Cookie 已失效", $"{source}：{reason}", cancellationToken);

    public Task NotifyGrabSucceededAsync(string libraryName, string seatName, CancellationToken cancellationToken = default)
        => notificationService.ShowSuccessAsync("抢座成功", $"{libraryName} {seatName}", cancellationToken);

    public Task NotifyTaskFailedAsync(string taskName, string reason, CancellationToken cancellationToken = default)
        => notificationService.ShowWarningAsync($"{taskName}失败", reason, cancellationToken);

    public Task SendTestEmailAsync(CookieExpiryEmailAlertSettings settings, CancellationToken cancellationToken = default)
        => notificationService.ShowInfoAsync("测试提醒", "Android 端不发送 SMTP 邮件。", cancellationToken);

    public Task SendTestLocalAlertAsync(CookieExpiryLocalAlertSettings settings, CancellationToken cancellationToken = default)
        => notificationService.ShowInfoAsync("测试提醒", "本地提醒可用。", cancellationToken);
}
