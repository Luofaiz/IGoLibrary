using IGoLibrary.Application.Abstractions;
using IGoLibrary.Application.Exceptions;
using IGoLibrary.Application.State;
using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Helpers;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Services;

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
    private sealed record TomorrowSeatMiss(TomorrowSeatMissKind Kind, int? ErrorCode, string RemoteMessage);
    private static readonly TimeSpan QueuePreheatLeadTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SubmittedConfirmationMinimumWait = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan SubmittedConfirmationMaximumWait = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan BusyRetryJitterMinimum = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan BusyRetryJitterMaximum = TimeSpan.FromMilliseconds(180);

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
            var scheduledStart = plan.ScheduledStart is null
                ? (DateTimeOffset?)null
                : ResolveNextScheduledStart(plan.ScheduledStart.Value, DateTimeOffset.Now);

            if (scheduledStart is not null)
            {
                await WaitUntilQueuePreheatAsync(scheduledStart.Value, cancellationToken);
            }

            var cookie = GetCurrentCookieOrThrow();
            var reservationSucceeded = new TaskCompletionSource<TrackedSeat>(TaskCreationOptions.RunContinuationsAsynchronously);
            queueSession = await StartQueueSessionAsync(cookie, reservationSucceeded, scheduledStart, cancellationToken);

            if (scheduledStart is not null)
            {
                await WaitUntilSubmissionWindowAsync(scheduledStart.Value, cancellationToken);
            }

            var cycle = 0;
            var requestCount = 0;
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
                cookie = GetCurrentCookieOrThrow();

                var attemptedSeatKeys = new HashSet<string>(StringComparer.Ordinal);
                var selectedSeatAccepted = false;
                var selectedSeatRetryRequested = false;
                var queueRefreshRequested = false;
                for (var offset = 0; !plan.UseRandomAvailableSeat && offset < plan.Seats.Count; offset++)
                {
                    var seat = plan.Seats[offset];
                    attemptedSeatKeys.Add(seat.SeatKey);

                    var outcome = await TrySubmitSeatAsync(
                        cookie,
                        plan.LibraryId,
                        seat,
                        MarkRequestSent,
                        reservationSucceeded.Task,
                        random,
                        cancellationToken);
                    cookie = GetCurrentCookieOrThrow();
                    if (outcome == TomorrowSeatSubmitOutcome.Submitted)
                    {
                        selectedSeatAccepted = true;
                    }
                    else if (outcome == TomorrowSeatSubmitOutcome.RetryLater)
                    {
                        selectedSeatRetryRequested = true;
                        if (offset + 1 < plan.Seats.Count)
                        {
                            await DelayAfterBusyRetryAsync(seat, random, cancellationToken);
                        }
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
                else if (plan.UseRandomAvailableSeat || (!selectedSeatAccepted && !selectedSeatRetryRequested))
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
        Task<TrackedSeat> reservationSucceeded,
        Random random,
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
                activityLogService.Write(LogEntryKind.Success, "Grab", $"{seat.SeatName} 明日预约请求已提交，短暂等待服务端成功确认。");
                var confirmationWait = RandomBetween(SubmittedConfirmationMinimumWait, SubmittedConfirmationMaximumWait, random);
                if (await WaitForReservationConfirmationAsync(reservationSucceeded, confirmationWait, cancellationToken))
                {
                    return TomorrowSeatSubmitOutcome.Submitted;
                }

                activityLogService.Write(
                    LogEntryKind.Info,
                    "Grab",
                    $"{seat.SeatName} 明日预约提交后 {confirmationWait.TotalMilliseconds:0}ms 内未收到成功确认，继续尝试下一个优先座位。");
                return TomorrowSeatSubmitOutcome.Submitted;
            }

            activityLogService.Write(LogEntryKind.Info, "Grab", $"{seat.SeatName} 明日预约请求未被接受，继续尝试其他座位。");
            return TomorrowSeatSubmitOutcome.Unavailable;
        }
        catch (Exception ex) when (TryGetExpectedPrereserveMiss(ex, out var miss))
        {
            activityLogService.Write(LogEntryKind.Info, "Grab", GetPrereserveMissMessage(miss, seat));
            return miss.Kind switch
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
            var message = plan.UseRandomAvailableSeat
                ? "随机空座模式暂未发现可预约的明日座位。"
                : "目标座位暂不可预约，且没有可随机尝试的候选座位。";
            activityLogService.Write(LogEntryKind.Info, "Grab", message);
            return TomorrowSeatSubmitOutcome.Unavailable;
        }

        cookie = GetCurrentCookieOrThrow();

        var finalOutcome = TomorrowSeatSubmitOutcome.Unavailable;
        foreach (var fallbackSeat in fallbackSeats)
        {
            attemptedSeatKeys.Add(fallbackSeat.SeatKey);
            var message = plan.UseRandomAvailableSeat
                ? $"随机选择明日空座 {fallbackSeat.SeatName}，正在提交预约请求。"
                : $"目标座位暂不可预约，随机尝试明日座位 {fallbackSeat.SeatName}。";
            activityLogService.Write(LogEntryKind.Info, "Grab", message);

            var outcome = await TrySubmitSeatAsync(
                cookie,
                plan.LibraryId,
                fallbackSeat,
                markRequestSent,
                reservationSucceeded,
                random,
                cancellationToken);
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
            var messageKind = message.IndicatesQueueReady
                ? LogEntryKind.Success
                : LogEntryKind.Info;
            activityLogService.Write(messageKind, "Grab", $"明日预约排队消息：{FormatQueueMessage(message)}");
        }

        if (message.IndicatesCookieInvalid)
        {
            throw new InvalidOperationException("明日预约排队返回 Cookie 无效。");
        }

        if (message.RequestsSessionRefresh)
        {
            var cookie = GetCurrentCookieOrThrow();
            await apiClient.ValidateCookieAsync(cookie, cancellationToken);
            activityLogService.Write(LogEntryKind.Info, "Grab", "明日预约排队预热已连接，等待正式排队成功消息。");
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
        DateTimeOffset? scheduledStart,
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

        SetRunning(scheduledStart is null ? "明日预约排队中。" : "明日预约排队预热中。");
        activityLogService.Write(
            LogEntryKind.Info,
            "Grab",
            scheduledStart is null
                ? "明日预约 websocket 排队已启动。"
                : $"明日预约 websocket 排队预热已启动，目标提交时间 {scheduledStart:yyyy-MM-dd HH:mm:ss}。");
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

        return await StartQueueSessionAsync(cookie, reservationSucceeded, scheduledStart: null, cancellationToken);
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

    private async Task WaitUntilQueuePreheatAsync(DateTimeOffset scheduledStart, CancellationToken cancellationToken)
    {
        var queuePreheatStart = scheduledStart - QueuePreheatLeadTime;
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var remaining = queuePreheatStart - now;
            if (remaining <= TimeSpan.Zero)
            {
                activityLogService.Write(
                    LogEntryKind.Info,
                    "Grab",
                    $"明日预约进入排队预热窗口，目标提交时间 {scheduledStart:yyyy-MM-dd HH:mm:ss}。");
                return;
            }

            activityLogService.Write(
                LogEntryKind.Info,
                "Grab",
                $"明日预约等待排队预热，预热时间 {queuePreheatStart:yyyy-MM-dd HH:mm:ss}，目标提交时间 {scheduledStart:yyyy-MM-dd HH:mm:ss}，还剩 {remaining:hh\\:mm\\:ss}。");
            await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task WaitUntilSubmissionWindowAsync(DateTimeOffset scheduledStart, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var remaining = scheduledStart - now;
            if (remaining <= TimeSpan.Zero)
            {
                activityLogService.Write(LogEntryKind.Success, "Grab", "明日预约已进入可提交窗口，开始按优先级提交座位请求。");
                return;
            }

            SetRunning("明日预约排队预热中。");
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    private static async Task<bool> WaitForReservationConfirmationAsync(
        Task<TrackedSeat> reservationSucceeded,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (reservationSucceeded.IsCompletedSuccessfully)
        {
            return true;
        }

        var completed = await Task.WhenAny(reservationSucceeded, Task.Delay(timeout, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return completed == reservationSucceeded && reservationSucceeded.IsCompletedSuccessfully;
    }

    private async Task DelayAfterBusyRetryAsync(TrackedSeat seat, Random random, CancellationToken cancellationToken)
    {
        var delay = RandomBetween(BusyRetryJitterMinimum, BusyRetryJitterMaximum, random);
        activityLogService.Write(
            LogEntryKind.Info,
            "Grab",
            $"{seat.SeatName} 明日预约遇到服务端忙碌，等待 {delay.TotalMilliseconds:0}ms 后继续下一个优先座位。");
        await Task.Delay(delay, cancellationToken);
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

    private static bool TryGetExpectedPrereserveMiss(Exception exception, out TomorrowSeatMiss miss)
    {
        miss = new TomorrowSeatMiss(TomorrowSeatMissKind.None, null, string.Empty);
        if (exception is not TraceIntApiException traceIntApiException)
        {
            return false;
        }

        var message = traceIntApiException.RemoteMessage;
        if (traceIntApiException.ErrorCode == 40006 ||
            ContainsAny(message, "请先排队", "先排队", "排队再选座", "需要排队", "queue"))
        {
            miss = BuildTomorrowSeatMiss(TomorrowSeatMissKind.QueueRequired, traceIntApiException);
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
            miss = BuildTomorrowSeatMiss(TomorrowSeatMissKind.RetryRequested, traceIntApiException);
            return true;
        }

        if (ContainsAny(message, "场馆满", "已满", "满员", "无空位", "没有空位", "暂无空位", "余座不足", "无可用座位", "无座", "full"))
        {
            miss = BuildTomorrowSeatMiss(TomorrowSeatMissKind.Unavailable, traceIntApiException);
            return true;
        }

        if (ContainsAny(message, "座位", "座席", "座号", "seat") &&
            ContainsAny(message, "已被", "被预约", "已预约", "占用", "不可预约", "不存在", "无效", "not available", "occupied"))
        {
            miss = BuildTomorrowSeatMiss(TomorrowSeatMissKind.Unavailable, traceIntApiException);
            return true;
        }

        return false;
    }

    private static TomorrowSeatMiss BuildTomorrowSeatMiss(TomorrowSeatMissKind kind, TraceIntApiException exception)
    {
        return new TomorrowSeatMiss(kind, exception.ErrorCode, TrimForLog(exception.RemoteMessage));
    }

    private static bool ContainsAny(string text, params string[] candidates)
    {
        return candidates.Any(candidate => text.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetPrereserveMissMessage(TomorrowSeatMiss miss, TrackedSeat seat)
    {
        var detail = FormatRemoteErrorForLog(miss);
        return miss.Kind switch
        {
            TomorrowSeatMissKind.QueueRequired =>
                $"{seat.SeatName} 明日预约需要重新排队，服务端返回：{detail}。",
            TomorrowSeatMissKind.RetryRequested =>
                $"{seat.SeatName} 明日预约服务端忙碌/要求重试，暂不切换随机座位，服务端返回：{detail}。",
            TomorrowSeatMissKind.Unavailable =>
                $"{seat.SeatName} 明日不可预约，继续尝试其他座位，服务端返回：{detail}。",
            _ => $"{seat.SeatName} 明日预约未命中，服务端返回：{detail}。"
        };
    }

    private static string FormatQueueMessage(PrereserveQueueMessage message)
    {
        var detail = TrimForLog(message.Message);
        if (message.Code is null && message.Data is null)
        {
            return detail;
        }

        return $"code={message.Code?.ToString() ?? "--"}, data={message.Data?.ToString() ?? "--"}, msg={detail}";
    }

    private static string FormatRemoteErrorForLog(TomorrowSeatMiss miss)
    {
        var message = string.IsNullOrWhiteSpace(miss.RemoteMessage) ? "无返回消息" : miss.RemoteMessage;
        return miss.ErrorCode is int code
            ? $"code={code}, msg={message}"
            : $"msg={message}";
    }

    private static string TrimForLog(string text)
    {
        var normalized = string.IsNullOrWhiteSpace(text) ? "无返回消息" : text.Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
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
