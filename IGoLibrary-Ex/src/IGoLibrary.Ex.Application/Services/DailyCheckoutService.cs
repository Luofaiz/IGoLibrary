using IGoLibrary.Ex.Application.Abstractions;
using IGoLibrary.Ex.Domain.Enums;
using IGoLibrary.Ex.Domain.Models;

namespace IGoLibrary.Ex.Application.Services;

public sealed class DailyCheckoutService(
    ISessionService sessionService,
    ITraceIntApiClient apiClient,
    IActivityLogService activityLogService) : IDailyCheckoutService
{
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public async Task<DailyCheckoutRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        SessionCredentials? session;
        try
        {
            session = sessionService.CurrentSession ?? await sessionService.RestoreAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"恢复登录会话失败：{ex.Message}");
        }

        if (session is null)
        {
            return Fail("无法恢复登录会话，请重新授权并勾选记住登录状态。");
        }

        ReservationInfo? reservation;
        try
        {
            reservation = await apiClient.GetReservationInfoAsync(session.Cookie, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"查询今日座位失败：{ex.Message}");
        }

        if (reservation is null)
        {
            activityLogService.Write(LogEntryKind.Info, "DailyCheckout", "当前没有今日座位，无需执行退座。");
            return DailyCheckoutRunResult.NoReservation();
        }

        if (string.IsNullOrWhiteSpace(reservation.ReservationToken))
        {
            return Fail("今日座位缺少退座所需的 sToken，无法执行自动退座。");
        }

        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var cookie = sessionService.CurrentSession?.Cookie ?? session.Cookie;
                var submitted = await apiClient.CancelReservationAsync(
                    cookie,
                    reservation.ReservationToken,
                    cancellationToken);

                cookie = sessionService.CurrentSession?.Cookie ?? cookie;
                var remainingReservation = await apiClient.GetReservationInfoAsync(cookie, cancellationToken);
                if (remainingReservation is null)
                {
                    activityLogService.Write(
                        LogEntryKind.Success,
                        "DailyCheckout",
                        $"{reservation.LibraryName} {reservation.SeatName} 已自动退座（第 {attempt} 次尝试）。");
                    return DailyCheckoutRunResult.Released(reservation.SeatName);
                }

                var submissionText = submitted ? "接口已响应，但复查时座位仍在" : "接口未返回成功结果";
                activityLogService.Write(
                    LogEntryKind.Warning,
                    "DailyCheckout",
                    $"第 {attempt} 次自动退座未确认成功：{submissionText}。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                activityLogService.Write(
                    LogEntryKind.Warning,
                    "DailyCheckout",
                    $"第 {attempt} 次自动退座失败：{ex.Message}");
            }

            if (attempt < MaximumAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        return Fail(lastException is null
            ? $"连续 {MaximumAttempts} 次退座后仍检测到今日座位。"
            : $"连续 {MaximumAttempts} 次退座失败：{lastException.Message}");
    }

    private DailyCheckoutRunResult Fail(string message)
    {
        activityLogService.Write(LogEntryKind.Error, "DailyCheckout", message);
        return DailyCheckoutRunResult.Failed(message);
    }
}
