using IGoLibrary.Application.Abstractions;
using IGoLibrary.Application.Exceptions;
using IGoLibrary.Application.State;
using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Helpers;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Services;

public sealed class OccupySeatCoordinator(
    ITraceIntApiClient apiClient,
    ISettingsService settingsService,
    INotificationService notificationService,
    ITaskAlertService taskAlertService,
    IActivityLogService activityLogService,
    AppRuntimeState runtimeState) : IOccupySeatCoordinator
{
    private static readonly TimeSpan ReleaseAfterCancellationDelay = TimeSpan.Zero;
    private static readonly TimeSpan ReReserveRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private CoordinatorStatus _status = CoordinatorStatus.Idle("占座");

    public event EventHandler<CoordinatorStatus>? StatusChanged;

    public CoordinatorStatus GetStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public Task StartAsync(OccupySeatPlan plan, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_runningTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("占座任务已在运行。");
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _status = new CoordinatorStatus(
                CoordinatorTaskState.Starting,
                "占座",
                "准备启动占座任务。",
                DateTimeOffset.Now,
                DateTimeOffset.Now);
            NotifyStatusChanged();
            _runningTask = RunAsync(plan, _cts.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? runningTask;
        lock (_gate)
        {
            if (_cts is null)
            {
                return;
            }

            _status = GetStatus() with
            {
                State = CoordinatorTaskState.Stopping,
                Message = "正在停止占座任务。",
                LastUpdatedAt = DateTimeOffset.Now
            };
            NotifyStatusChanged();
            _cts.Cancel();
            runningTask = _runningTask;
        }

        if (runningTask is not null)
        {
            try
            {
                await runningTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunAsync(OccupySeatPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            SetRunning("占座任务已启动。");
            activityLogService.Write(LogEntryKind.Success, "Occupy", "占座任务已启动。");
            var random = new Random();
            while (!cancellationToken.IsCancellationRequested)
            {
                var cookie = GetCurrentCookieOrThrow();
                var info = await apiClient.GetReservationInfoAsync(cookie, cancellationToken);
                if (info is null)
                {
                    throw new InvalidOperationException("当前没有可续占的预约。");
                }

                runtimeState.CurrentReservation = info;
                if (info.IsCheckedIn)
                {
                    activityLogService.Write(LogEntryKind.Info, "Occupy", $"{info.SeatName} 已签到，状态为学习中，停止占座任务。");
                    Complete("已签到，学习中，停止占座任务。");
                    return;
                }

                var scheduledReReserveTime = plan.TriggerMode == OccupyReReserveTriggerMode.ScheduledTime
                    ? plan.ScheduledReReserveTime
                    : null;

                if (!ReservationTimeHelper.ShouldReReserve(
                        info.ExpirationTime,
                        DateTimeOffset.Now,
                        plan.ReReserveLeadTime,
                        scheduledReReserveTime))
                {
                    var delay = plan.RefreshMode == RefreshMode.FixedTenSeconds
                        ? TimeSpan.FromSeconds(10)
                        : TimeSpan.FromSeconds(random.Next(10, 21));
                    var triggerRemaining = ReservationTimeHelper.GetReReserveTriggerRemaining(
                        info.ExpirationTime,
                        DateTimeOffset.Now,
                        plan.ReReserveLeadTime,
                        scheduledReReserveTime);
                    activityLogService.Write(LogEntryKind.Info, "Occupy", $"距离重约触发还有 {triggerRemaining.TotalSeconds:0} 秒，{delay.TotalSeconds:0} 秒后继续检测。");
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                activityLogService.Write(LogEntryKind.Warning, "Occupy", "预约即将过期，开始取消并重新预约。");
                var cancelled = await apiClient.CancelReservationAsync(cookie, info.ReservationToken, cancellationToken);
                if (!cancelled)
                {
                    throw new InvalidOperationException("取消预约失败。");
                }

                await Task.Delay(ReleaseAfterCancellationDelay, cancellationToken);
                var reservedSeat = await TryReserveAgainAsync(GetCurrentCookieOrThrow(), info, random, cancellationToken);
                if (reservedSeat is null)
                {
                    throw new InvalidOperationException("重新预约失败，已达到重试上限。");
                }

                activityLogService.Write(LogEntryKind.Success, "Occupy", $"{reservedSeat.SeatName} 已重新预约成功。");
                await notificationService.ShowSuccessAsync("占座成功", $"{reservedSeat.SeatName} 已重新预约。", cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Complete("占座任务已停止。");
        }
        catch (Exception ex)
        {
            Fail($"占座任务失败：{ex.Message}");
            activityLogService.Write(LogEntryKind.Error, "Occupy", ex.Message);
            if (CookieExpiryDetector.IsKnownExpiredCookieException(ex, runtimeState.Session?.Cookie))
            {
                await taskAlertService.NotifyCookieExpiredAsync("占座轮询", ex.Message, CancellationToken.None);
                return;
            }

            await taskAlertService.NotifyTaskFailedAsync("占座", ex.Message, CancellationToken.None);
        }
    }

    private void SetRunning(string message)
    {
        lock (_gate)
        {
            _status = new CoordinatorStatus(
                CoordinatorTaskState.Running,
                "占座",
                message,
                _status.StartedAt ?? DateTimeOffset.Now,
                DateTimeOffset.Now);
        }

        NotifyStatusChanged();
    }

    private void Complete(string message)
    {
        lock (_gate)
        {
            _cts = null;
            _runningTask = null;
            _status = new CoordinatorStatus(
                CoordinatorTaskState.Completed,
                "占座",
                message,
                _status.StartedAt,
                DateTimeOffset.Now);
        }

        NotifyStatusChanged();
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _cts = null;
            _runningTask = null;
            _status = new CoordinatorStatus(
                CoordinatorTaskState.Failed,
                "占座",
                message,
                _status.StartedAt,
                DateTimeOffset.Now);
        }

        NotifyStatusChanged();
    }

    private void NotifyStatusChanged()
    {
        StatusChanged?.Invoke(this, GetStatus());
    }

    private string GetCurrentCookieOrThrow()
    {
        var cookie = runtimeState.Session?.Cookie ?? throw new InvalidOperationException("当前未登录。");
        if (CookieExpiryDetector.TryGetExpirationTime(cookie, out var expirationTime) &&
            expirationTime <= DateTimeOffset.Now)
        {
            throw new InvalidOperationException(CookieExpiryDetector.BuildExpiredMessage(expirationTime));
        }

        return cookie;
    }

    private async Task<TrackedSeat?> TryReserveAgainAsync(
        string cookie,
        ReservationInfo info,
        Random random,
        CancellationToken cancellationToken)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);
        var maxAttempts = Math.Max(1, settings.RetryCount + 1);
        var shouldTryFallbackSeats = false;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var reserved = await apiClient.ReserveSeatAsync(cookie, info.LibraryId, info.SeatKey, cancellationToken);
                if (reserved)
                {
                    if (attempt > 1)
                    {
                        activityLogService.Write(LogEntryKind.Success, "Occupy", $"第 {attempt} 次重新预约尝试成功。");
                    }

                    return new TrackedSeat(info.SeatKey, info.SeatName);
                }

                shouldTryFallbackSeats = true;
            }
            catch (Exception ex) when (TryGetExpectedReserveMiss(ex, out var missKind))
            {
                activityLogService.Write(LogEntryKind.Info, "Occupy", GetReserveMissMessage(missKind, info.SeatName));
                if (missKind == OccupyReserveMissKind.Unavailable)
                {
                    shouldTryFallbackSeats = true;
                }
            }

            if (attempt >= maxAttempts)
            {
                break;
            }

            activityLogService.Write(LogEntryKind.Warning, "Occupy", $"第 {attempt} 次重新预约失败，{ReReserveRetryDelay.TotalSeconds:0} 秒后继续重试。");
            await Task.Delay(ReReserveRetryDelay, cancellationToken);
            cookie = GetCurrentCookieOrThrow();
        }

        return shouldTryFallbackSeats
            ? await TryReserveFallbackSeatAsync(cookie, info, random, cancellationToken)
            : null;
    }

    private async Task<TrackedSeat?> TryReserveFallbackSeatAsync(
        string cookie,
        ReservationInfo originalInfo,
        Random random,
        CancellationToken cancellationToken)
    {
        SetRunning("原座位暂不可用，正在尝试同场馆其他空座。");
        var fallbackSeats = await SelectFallbackSeatsAsync(cookie, originalInfo, random, cancellationToken);
        if (fallbackSeats.Count == 0)
        {
            activityLogService.Write(LogEntryKind.Warning, "Occupy", "原座位未能重新预约，当前场馆没有可兜底尝试的空座。");
            return null;
        }

        foreach (var seat in fallbackSeats)
        {
            cookie = GetCurrentCookieOrThrow();
            try
            {
                var reserved = await apiClient.ReserveSeatAsync(cookie, originalInfo.LibraryId, seat.SeatKey, cancellationToken);
                if (reserved)
                {
                    activityLogService.Write(LogEntryKind.Success, "Occupy", $"原座位已被占用，已改为预约 {seat.SeatName}。");
                    return seat;
                }

                activityLogService.Write(LogEntryKind.Info, "Occupy", $"{seat.SeatName} 兜底预约未被接受，继续尝试其他空座。");
            }
            catch (Exception ex) when (TryGetExpectedReserveMiss(ex, out var missKind))
            {
                activityLogService.Write(LogEntryKind.Info, "Occupy", GetReserveMissMessage(missKind, seat.SeatName));
                if (missKind == OccupyReserveMissKind.RetryRequested)
                {
                    await Task.Delay(ReReserveRetryDelay, cancellationToken);
                }
            }
        }

        return null;
    }

    private async Task<List<TrackedSeat>> SelectFallbackSeatsAsync(
        string cookie,
        ReservationInfo originalInfo,
        Random random,
        CancellationToken cancellationToken)
    {
        LibraryLayout layout;
        try
        {
            layout = await apiClient.GetLibraryLayoutAsync(cookie, originalInfo.LibraryId, cancellationToken);
            runtimeState.CurrentLayout = layout;
        }
        catch (Exception ex) when (!CookieExpiryDetector.IsKnownExpiredCookieException(ex, runtimeState.Session?.Cookie))
        {
            activityLogService.Write(LogEntryKind.Warning, "Occupy", $"读取座位图失败，尝试使用本地缓存兜底：{ex.Message}");
            if (runtimeState.CurrentLayout?.LibraryId != originalInfo.LibraryId)
            {
                return [];
            }

            layout = runtimeState.CurrentLayout;
        }

        var candidates = layout.Seats
            .Where(seat => seat.IsAvailable)
            .Where(seat => !string.IsNullOrWhiteSpace(seat.SeatKey))
            .Where(seat => !string.Equals(seat.SeatKey, originalInfo.SeatKey, StringComparison.Ordinal))
            .Select(seat => new TrackedSeat(
                seat.SeatKey,
                string.IsNullOrWhiteSpace(seat.SeatName) ? seat.SeatKey : seat.SeatName))
            .GroupBy(seat => seat.SeatKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        Shuffle(candidates, random);
        return candidates;
    }

    private static void Shuffle<T>(IList<T> items, Random random)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }

    private static bool TryGetExpectedReserveMiss(Exception exception, out OccupyReserveMissKind missKind)
    {
        missKind = OccupyReserveMissKind.None;
        string message;
        if (exception is TraceIntApiException traceIntApiException)
        {
            message = traceIntApiException.RemoteMessage;
        }
        else if (exception is InvalidOperationException)
        {
            message = exception.Message;
        }
        else
        {
            return false;
        }

        if (ContainsAny(message, "请重新尝试", "重新尝试", "重试", "服务器繁忙", "繁忙", "频繁", "too many", "try again", "busy"))
        {
            missKind = OccupyReserveMissKind.RetryRequested;
            return true;
        }

        if (ContainsAny(message, "场馆满", "已满", "满员", "无空位", "没有空位", "暂无空位", "余座不足", "无可用座位", "无座", "full"))
        {
            missKind = OccupyReserveMissKind.Unavailable;
            return true;
        }

        if (ContainsAny(message, "座位", "座席", "座号", "seat") &&
            ContainsAny(message, "已被", "已经被", "被预约", "被预定", "被人预约", "被人预定", "已预约", "已预定", "占用", "不可预约", "不存在", "无效", "not available", "occupied"))
        {
            missKind = OccupyReserveMissKind.Unavailable;
            return true;
        }

        return false;
    }

    private static string GetReserveMissMessage(OccupyReserveMissKind missKind, string seatName)
    {
        return missKind switch
        {
            OccupyReserveMissKind.RetryRequested =>
                $"{seatName} 重新预约返回服务器繁忙或需要重试，稍后继续尝试。",
            OccupyReserveMissKind.Unavailable =>
                $"{seatName} 暂不可预约，将短暂重试原座；若仍不可用会尝试同场馆其他空座。",
            _ => $"{seatName} 重新预约未命中。"
        };
    }

    private static bool ContainsAny(string text, params string[] candidates)
    {
        return candidates.Any(candidate => text.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private enum OccupyReserveMissKind
    {
        None = 0,
        Unavailable = 1,
        RetryRequested = 2
    }
}
