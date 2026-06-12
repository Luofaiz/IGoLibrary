using IGoLibrary.Ex.Application.Abstractions;
using IGoLibrary.Ex.Application.Exceptions;
using IGoLibrary.Ex.Application.State;
using IGoLibrary.Ex.Domain.Enums;
using IGoLibrary.Ex.Domain.Helpers;
using IGoLibrary.Ex.Domain.Models;

namespace IGoLibrary.Ex.Application.Services;

public sealed class TomorrowReservationCoordinator(
    ITraceIntApiClient apiClient,
    IPrereserveQueueClient queueClient,
    ITaskAlertService taskAlertService,
    IActivityLogService activityLogService,
    AppRuntimeState runtimeState) : ITomorrowReservationCoordinator
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private CoordinatorStatus _status = CoordinatorStatus.Idle("明日预约");
    private sealed record QueueSession(CancellationTokenSource Cancellation, Task Task);

    public event EventHandler<CoordinatorStatus>? StatusChanged;

    public CoordinatorStatus GetStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public Task StartAsync(TomorrowReservationPlan plan, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_runningTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("明日预约任务已在运行。");
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _status = new CoordinatorStatus(
                CoordinatorTaskState.Starting,
                "明日预约",
                "准备启动明日预约任务。",
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
                Message = "正在停止明日预约任务。",
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

    private async Task RunAsync(TomorrowReservationPlan plan, CancellationToken cancellationToken)
    {
        QueueSession? queueSession = null;
        try
        {
            if (plan.ScheduledStart is not null)
            {
                await WaitUntilScheduledStartAsync(plan.ScheduledStart.Value, cancellationToken);
            }

            var cookie = GetCurrentCookieOrThrow();
            var reservationSucceeded = new TaskCompletionSource<TrackedSeat>(TaskCreationOptions.RunContinuationsAsynchronously);
            queueSession = await StartQueueSessionAsync(cookie, reservationSucceeded, cancellationToken);

            var cycle = 0;
            var requestCount = 0;
            var seatStartIndex = 0;
            DateTimeOffset? lastRequestAt = null;
            var random = new Random();

            while (!cancellationToken.IsCancellationRequested)
            {
                cycle++;
                UpdateRunningMetrics("明日预约请求循环运行中。", cycle, requestCount, lastRequestAt);
                cookie = GetCurrentCookieOrThrow();

                void MarkRequestSent()
                {
                    requestCount++;
                    lastRequestAt = DateTimeOffset.Now;
                    UpdateRunningMetrics("明日预约请求循环运行中。", cycle, requestCount, lastRequestAt);
                }

                MarkRequestSent();
                await apiClient.RefreshPrereservePageAsync(cookie, cancellationToken);

                var attemptedSeatKeys = new HashSet<string>(StringComparer.Ordinal);
                var selectedSeatAccepted = false;
                var selectedSeatRetryRequested = false;
                var queueRefreshRequested = false;
                for (var offset = 0; offset < plan.Seats.Count; offset++)
                {
                    var index = (seatStartIndex + offset) % plan.Seats.Count;
                    var seat = plan.Seats[index];
                    attemptedSeatKeys.Add(seat.SeatKey);

                    var outcome = await TrySubmitSeatAsync(cookie, plan.LibraryId, seat, MarkRequestSent, cancellationToken);
                    cookie = GetCurrentCookieOrThrow();
                    if (outcome == TomorrowSeatSubmitOutcome.Submitted)
                    {
                        selectedSeatAccepted = true;
                    }
                    else if (outcome == TomorrowSeatSubmitOutcome.RetryLater)
                    {
                        selectedSeatRetryRequested = true;
                    }
                    else if (outcome == TomorrowSeatSubmitOutcome.QueueRequired)
                    {
                        selectedSeatRetryRequested = true;
                        queueRefreshRequested = true;
                        break;
                    }

                    if (reservationSucceeded.Task.IsCompletedSuccessfully)
                    {
                        var reservedSeat = reservationSucceeded.Task.Result;
                        await CompleteSuccessfullyAsync(plan, reservedSeat, cancellationToken);
                        return;
                    }
                }

                if (queueRefreshRequested)
                {
                    cookie = GetCurrentCookieOrThrow();
                    queueSession = await RestartQueueSessionAsync(queueSession, cookie, reservationSucceeded, cancellationToken);
                }
                else if (!selectedSeatAccepted && !selectedSeatRetryRequested)
                {
                    var fallbackOutcome = await TrySubmitRandomFallbackSeatsAsync(
                        cookie,
                        plan,
                        attemptedSeatKeys,
                        random,
                        MarkRequestSent,
                        reservationSucceeded.Task,
                        cancellationToken);

                    if (fallbackOutcome == TomorrowSeatSubmitOutcome.QueueRequired)
                    {
                        cookie = GetCurrentCookieOrThrow();
                        queueSession = await RestartQueueSessionAsync(queueSession, cookie, reservationSucceeded, cancellationToken);
                    }
                }

                if (reservationSucceeded.Task.IsCompletedSuccessfully)
                {
                    var reservedSeat = reservationSucceeded.Task.Result;
                    await CompleteSuccessfullyAsync(plan, reservedSeat, cancellationToken);
                    return;
                }

                seatStartIndex = (seatStartIndex + 1) % plan.Seats.Count;
                var delay = RandomBetween(plan.PollingStrategy.MinimumDelay, plan.PollingStrategy.MaximumDelay, random);
                await Task.Delay(delay, cancellationToken);

                if (reservationSucceeded.Task.IsCompletedSuccessfully)
                {
                    var reservedSeat = reservationSucceeded.Task.Result;
                    await CompleteSuccessfullyAsync(plan, reservedSeat, cancellationToken);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Complete("明日预约任务已停止。");
        }
        catch (Exception ex)
        {
            Fail($"明日预约任务失败：{ex.Message}");
            activityLogService.Write(LogEntryKind.Error, "Grab", ex.Message);
            if (CookieExpiryDetector.IsKnownExpiredCookieException(ex, runtimeState.Session?.Cookie))
            {
                await taskAlertService.NotifyCookieExpiredAsync("明日预约", ex.Message, CancellationToken.None);
                return;
            }

            await taskAlertService.NotifyTaskFailedAsync("明日预约", ex.Message, CancellationToken.None);
        }
        finally
        {
            if (queueSession is not null)
            {
                await StopQueueSessionAsync(queueSession);
            }
        }
    }

    private async Task<TomorrowSeatSubmitOutcome> TrySubmitSeatAsync(
        string cookie,
        int libraryId,
        TrackedSeat seat,
        Action markRequestSent,
        CancellationToken cancellationToken)
    {
        try
        {
            markRequestSent();
            var result = await apiClient.SavePrereserveSeatAsync(cookie, libraryId, seat.SeatKey, cancellationToken);
            runtimeState.Session = runtimeState.Session is null
                ? null
                : runtimeState.Session with { Cookie = result.UpdatedCookie };

            if (result.Submitted)
            {
                activityLogService.Write(LogEntryKind.Success, "Grab", $"{seat.SeatName} 明日预约请求已提交。");
                return TomorrowSeatSubmitOutcome.Submitted;
            }

            activityLogService.Write(LogEntryKind.Info, "Grab", $"{seat.SeatName} 明日预约请求未被接受，继续尝试其他座位。");
            return TomorrowSeatSubmitOutcome.Unavailable;
        }
        catch (Exception ex) when (TryGetExpectedPrereserveMiss(ex, out var missKind))
        {
            activityLogService.Write(LogEntryKind.Info, "Grab", GetPrereserveMissMessage(missKind, seat));
            return missKind switch
            {
                TomorrowSeatMissKind.QueueRequired => TomorrowSeatSubmitOutcome.QueueRequired,
                TomorrowSeatMissKind.RetryRequested => TomorrowSeatSubmitOutcome.RetryLater,
                _ => TomorrowSeatSubmitOutcome.Unavailable
            };
        }
    }

    private async Task<TomorrowSeatSubmitOutcome> TrySubmitRandomFallbackSeatsAsync(
        string cookie,
        TomorrowReservationPlan plan,
        HashSet<string> attemptedSeatKeys,
        Random random,
        Action markRequestSent,
        Task<TrackedSeat> reservationSucceeded,
        CancellationToken cancellationToken)
    {
        var fallbackSeats = await SelectRandomFallbackSeatsAsync(
            cookie,
            plan,
            attemptedSeatKeys,
            random,
            markRequestSent,
            cancellationToken);
        if (fallbackSeats.Count == 0)
        {
            activityLogService.Write(LogEntryKind.Info, "Grab", "目标座位暂不可预约，且没有可随机尝试的候选座位。");
            return TomorrowSeatSubmitOutcome.Unavailable;
        }

        var finalOutcome = TomorrowSeatSubmitOutcome.Unavailable;
        foreach (var fallbackSeat in fallbackSeats)
        {
            attemptedSeatKeys.Add(fallbackSeat.SeatKey);
            activityLogService.Write(
                LogEntryKind.Info,
                "Grab",
                $"目标座位暂不可预约，随机尝试明日座位 {fallbackSeat.SeatName}。");

            var outcome = await TrySubmitSeatAsync(cookie, plan.LibraryId, fallbackSeat, markRequestSent, cancellationToken);
            if (reservationSucceeded.IsCompletedSuccessfully ||
                outcome is TomorrowSeatSubmitOutcome.Submitted or TomorrowSeatSubmitOutcome.RetryLater or TomorrowSeatSubmitOutcome.QueueRequired)
            {
                return outcome;
            }

            finalOutcome = outcome;
            cookie = GetCurrentCookieOrThrow();
        }

        return finalOutcome;
    }

    private async Task<List<TrackedSeat>> SelectRandomFallbackSeatsAsync(
        string cookie,
        TomorrowReservationPlan plan,
        HashSet<string> attemptedSeatKeys,
        Random random,
        Action markRequestSent,
        CancellationToken cancellationToken)
    {
        LibraryLayout? layout;
        var onlyAvailableSeats = true;
        try
        {
            markRequestSent();
            layout = await apiClient.GetPrereserveLibraryLayoutAsync(cookie, plan.LibraryId, cancellationToken);
            runtimeState.CurrentLayout = layout;
        }
        catch (Exception ex) when (ex is TraceIntApiException or InvalidOperationException or KeyNotFoundException)
        {
            activityLogService.Write(LogEntryKind.Warning, "Grab", $"读取明日座位图失败，退回普通座位图随机尝试：{ex.Message}");
            onlyAvailableSeats = false;
            layout = runtimeState.CurrentLayout?.LibraryId == plan.LibraryId
                ? runtimeState.CurrentLayout
                : null;
            if (layout is null)
            {
                markRequestSent();
                layout = await apiClient.GetLibraryLayoutAsync(cookie, plan.LibraryId, cancellationToken);
                runtimeState.CurrentLayout = layout;
            }
        }

        var candidates = layout.Seats
            .Where(seat => !string.IsNullOrWhiteSpace(seat.SeatKey))
            .Where(seat => !attemptedSeatKeys.Contains(seat.SeatKey))
            .Where(seat => !onlyAvailableSeats || seat.IsAvailable)
            .Select(seat => new TrackedSeat(
                seat.SeatKey,
                string.IsNullOrWhiteSpace(seat.SeatName) ? seat.SeatKey : seat.SeatName))
            .GroupBy(seat => seat.SeatKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        Shuffle(candidates, random);
        return candidates;
    }

    private async Task HandleQueueMessageAsync(
        PrereserveQueueMessage message,
        TaskCompletionSource queueReady,
        TaskCompletionSource<TrackedSeat> reservationSucceeded,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(message.Message) &&
            (!message.IndicatesQueueReady || !queueReady.Task.IsCompleted))
        {
            activityLogService.Write(LogEntryKind.Info, "Grab", $"明日预约排队消息：{message.Message}");
        }

        if (message.IndicatesCookieInvalid)
        {
            throw new InvalidOperationException("明日预约排队返回 Cookie 无效。");
        }

        if (message.RequestsSessionRefresh)
        {
            var cookie = GetCurrentCookieOrThrow();
            await apiClient.ValidateCookieAsync(cookie, cancellationToken);
            queueReady.TrySetResult();
        }

        if (message.IndicatesQueueReady)
        {
            queueReady.TrySetResult();
        }

        if (message.IndicatesSuccess)
        {
            reservationSucceeded.TrySetResult(ResolveSuccessSeat(message.Message));
        }
    }

    private async Task<QueueSession> StartQueueSessionAsync(
        string cookie,
        TaskCompletionSource<TrackedSeat> reservationSucceeded,
        CancellationToken cancellationToken)
    {
        var queueReadyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queueReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueTask = queueClient.RunAsync(
            cookie,
            async (message, messageToken) =>
            {
                await HandleQueueMessageAsync(message, queueReady, reservationSucceeded, messageToken);
            },
            queueReadyCts.Token);
        var session = new QueueSession(queueReadyCts, queueTask);

        SetRunning("明日预约排队中。");
        activityLogService.Write(LogEntryKind.Info, "Grab", "明日预约 websocket 排队已启动。");
        try
        {
            await WaitForQueueHandshakeAsync(queueReady.Task, queueTask, cancellationToken);
            return session;
        }
        catch
        {
            await StopQueueSessionAsync(session);
            throw;
        }
    }

    private async Task<QueueSession> RestartQueueSessionAsync(
        QueueSession? currentSession,
        string cookie,
        TaskCompletionSource<TrackedSeat> reservationSucceeded,
        CancellationToken cancellationToken)
    {
        activityLogService.Write(LogEntryKind.Warning, "Grab", "明日预约排队状态失效，正在重新排队。");
        if (currentSession is not null)
        {
            await StopQueueSessionAsync(currentSession);
        }

        return await StartQueueSessionAsync(cookie, reservationSucceeded, cancellationToken);
    }

    private async Task StopQueueSessionAsync(QueueSession session)
    {
        try
        {
            session.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await session.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            activityLogService.Write(LogEntryKind.Warning, "Grab", "明日预约排队连接停止超时，已继续结束任务。");
        }
        catch (Exception ex)
        {
            activityLogService.Write(LogEntryKind.Warning, "Grab", $"明日预约排队连接已关闭：{ex.Message}");
        }
        finally
        {
            try
            {
                session.Cancellation.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private TrackedSeat ResolveSuccessSeat(string message)
    {
        var layout = runtimeState.CurrentLayout;
        if (layout is not null)
        {
            var matchedSeat = layout.Seats
                .Where(seat => !string.IsNullOrWhiteSpace(seat.SeatName))
                .OrderByDescending(seat => seat.SeatName.Length)
                .FirstOrDefault(seat => message.Contains(seat.SeatName, StringComparison.Ordinal));
            if (matchedSeat is not null)
            {
                return new TrackedSeat(matchedSeat.SeatKey, matchedSeat.SeatName);
            }
        }

        return new TrackedSeat(string.Empty, "目标座位");
    }

    private static async Task WaitForQueueHandshakeAsync(Task readyTask, Task queueTask, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(readyTask, queueTask);
        if (completed == queueTask)
        {
            await queueTask;
            throw new InvalidOperationException("明日预约排队连接已关闭。");
        }

        await readyTask.WaitAsync(cancellationToken);
    }

    private async Task CompleteSuccessfullyAsync(
        TomorrowReservationPlan plan,
        TrackedSeat reservedSeat,
        CancellationToken cancellationToken)
    {
        activityLogService.Write(LogEntryKind.Success, "Grab", $"{reservedSeat.SeatName} 明日预约成功。");
        await taskAlertService.NotifyGrabSucceededAsync(plan.LibraryName, $"{reservedSeat.SeatName}（明日预约）", cancellationToken);
        Complete("已成功预约明日目标座位。");
    }

    private async Task WaitUntilScheduledStartAsync(TimeOnly scheduledStart, CancellationToken cancellationToken)
    {
        var targetStart = ResolveNextScheduledStart(scheduledStart, DateTimeOffset.Now);
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var remaining = targetStart - now;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            activityLogService.Write(
                LogEntryKind.Info,
                "Grab",
                $"明日预约等待中，目标启动时间 {targetStart:yyyy-MM-dd HH:mm:ss}，还剩 {remaining:hh\\:mm\\:ss}。");
            await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    internal static DateTimeOffset ResolveNextScheduledStart(TimeOnly scheduledStart, DateTimeOffset now)
    {
        var todayScheduledStart = new DateTimeOffset(
            now.Date.Add(scheduledStart.ToTimeSpan()),
            now.Offset);

        return todayScheduledStart < now
            ? todayScheduledStart.AddDays(1)
            : todayScheduledStart;
    }

    private static TimeSpan RandomBetween(TimeSpan minimum, TimeSpan maximum, Random random)
    {
        if (maximum <= minimum)
        {
            return minimum;
        }

        var delta = maximum - minimum;
        var offset = random.NextDouble() * delta.TotalMilliseconds;
        return minimum + TimeSpan.FromMilliseconds(offset);
    }

    private static void Shuffle<T>(IList<T> items, Random random)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }

    private static bool TryGetExpectedPrereserveMiss(Exception exception, out TomorrowSeatMissKind missKind)
    {
        missKind = TomorrowSeatMissKind.None;
        if (exception is not TraceIntApiException traceIntApiException)
        {
            return false;
        }

        var message = traceIntApiException.RemoteMessage;
        if (traceIntApiException.ErrorCode == 40006 ||
            ContainsAny(message, "请先排队", "先排队", "排队再选座", "需要排队", "queue"))
        {
            missKind = TomorrowSeatMissKind.QueueRequired;
            return true;
        }

        if (message.Contains("服务器繁忙", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("繁忙", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("稍后", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("频繁", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("too many", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("busy", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("重新", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("重试", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("try again", StringComparison.OrdinalIgnoreCase))
        {
            missKind = TomorrowSeatMissKind.RetryRequested;
            return true;
        }

        if (ContainsAny(message, "场馆满", "已满", "满员", "无空位", "没有空位", "暂无空位", "余座不足", "无可用座位", "无座", "full"))
        {
            missKind = TomorrowSeatMissKind.Unavailable;
            return true;
        }

        if (ContainsAny(message, "座位", "座席", "座号", "seat") &&
            ContainsAny(message, "已被", "被预约", "已预约", "占用", "不可预约", "不存在", "无效", "not available", "occupied"))
        {
            missKind = TomorrowSeatMissKind.Unavailable;
            return true;
        }

        return false;
    }

    private static bool ContainsAny(string text, params string[] candidates)
    {
        return candidates.Any(candidate => text.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetPrereserveMissMessage(TomorrowSeatMissKind missKind, TrackedSeat seat)
    {
        return missKind switch
        {
            TomorrowSeatMissKind.QueueRequired =>
                $"{seat.SeatName} 明日预约排队状态已失效，需要重新排队。",
            TomorrowSeatMissKind.RetryRequested =>
                $"{seat.SeatName} 明日预约返回服务器繁忙或需要重试，暂不切换随机座位。",
            TomorrowSeatMissKind.Unavailable =>
                $"{seat.SeatName} 明日不可预约，继续尝试其他座位。",
            _ => $"{seat.SeatName} 明日预约未命中。"
        };
    }

    private void SetRunning(string message)
    {
        UpdateRunningMetrics(message, _status.PollCount, _status.RequestCount, _status.LastRequestAt);
    }

    private void UpdateRunningMetrics(
        string message,
        int pollCount,
        int requestCount,
        DateTimeOffset? lastRequestAt)
    {
        lock (_gate)
        {
            _status = new CoordinatorStatus(
                CoordinatorTaskState.Running,
                "明日预约",
                message,
                _status.StartedAt ?? DateTimeOffset.Now,
                DateTimeOffset.Now,
                pollCount,
                requestCount,
                lastRequestAt);
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
                "明日预约",
                message,
                _status.StartedAt,
                DateTimeOffset.Now,
                _status.PollCount,
                _status.RequestCount,
                _status.LastRequestAt);
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
                "明日预约",
                message,
                _status.StartedAt,
                DateTimeOffset.Now,
                _status.PollCount,
                _status.RequestCount,
                _status.LastRequestAt);
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

    private enum TomorrowSeatSubmitOutcome
    {
        Submitted = 0,
        Unavailable = 1,
        RetryLater = 2,
        QueueRequired = 3
    }

    private enum TomorrowSeatMissKind
    {
        None = 0,
        Unavailable = 1,
        RetryRequested = 2,
        QueueRequired = 3
    }
}
