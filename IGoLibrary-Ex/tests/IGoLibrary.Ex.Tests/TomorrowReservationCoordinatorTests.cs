using IGoLibrary.Ex.Application.Services;
using IGoLibrary.Ex.Application.State;
using IGoLibrary.Ex.Application.Exceptions;
using IGoLibrary.Ex.Domain.Enums;
using IGoLibrary.Ex.Domain.Models;

namespace IGoLibrary.Ex.Tests;

public sealed class TomorrowReservationCoordinatorTests
{
    [Fact]
    public async Task StartAsync_BeginsSeatSubmission_WhenQueueReadyMessageArrives()
    {
        var saveCalls = 0;
        var submitSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopQueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apiClient = new FakeTraceIntApiClient
        {
            OnRefreshPrereservePageAsync = (_, _) => Task.CompletedTask,
            OnSavePrereserveSeatAsync = (cookie, libraryId, seatKey, _) =>
            {
                saveCalls++;
                submitSeen.TrySetResult();
                return Task.FromResult(new PrereserveSaveResult(true, cookie));
            }
        };
        var queueClient = new FakePrereserveQueueClient
        {
            OnRunAsync = async (onMessageAsync, cancellationToken) =>
            {
                await onMessageAsync(new PrereserveQueueMessage(
                    "prereserve/queue",
                    "排队成功！请在2分钟内选择座位，否则需要重新排队。",
                    0,
                    0,
                    string.Empty), cancellationToken);
                await stopQueue.Task.WaitAsync(cancellationToken);
            }
        };
        var runtimeState = new AppRuntimeState
        {
            Session = new SessionCredentials("Authorization=a; SERVERID=b", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var coordinator = new TomorrowReservationCoordinator(
            apiClient,
            queueClient,
            new FakeTaskAlertService(),
            new ActivityLogService(),
            runtimeState);
        var plan = new TomorrowReservationPlan(
            117580,
            "自科阅览区",
            [new TrackedSeat("10,79", "225")],
            GrabMode.Aggressive,
            new GrabSeatPollingStrategy(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
                50,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10)),
            null);

        await coordinator.StartAsync(plan);
        await submitSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.StopAsync();
        stopQueue.TrySetResult();

        Assert.Equal(1, saveCalls);
    }

    [Fact]
    public async Task StartAsync_SubmitsRandomFallbackSeat_WhenSelectedSeatIsUnavailableTomorrow()
    {
        var submittedSeatKeys = new List<string>();
        var fallbackSubmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopQueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apiClient = new FakeTraceIntApiClient
        {
            OnRefreshPrereservePageAsync = (_, _) => Task.CompletedTask,
            OnGetPrereserveLibraryLayoutAsync = (_, _, _) => Task.FromResult(new LibraryLayout(
                117580,
                "自科阅览区",
                "3",
                true,
                4,
                1,
                0,
                [
                    new SeatSnapshot("selected", "225", false, 0, 0),
                    new SeatSnapshot("occupied-tomorrow", "226", true, 1, 0),
                    new SeatSnapshot("fallback-a", "226", false, 1, 0),
                    new SeatSnapshot("fallback-b", "227", false, 2, 0)
                ])),
            OnSavePrereserveSeatAsync = (cookie, libraryId, seatKey, _) =>
            {
                submittedSeatKeys.Add(seatKey);
                if (seatKey == "selected")
                {
                    throw new TraceIntApiException("座位已被预约", 1, "座位已被预约");
                }

                fallbackSubmitted.TrySetResult();
                return Task.FromResult(new PrereserveSaveResult(true, cookie));
            }
        };
        var queueClient = new FakePrereserveQueueClient
        {
            OnRunAsync = async (onMessageAsync, cancellationToken) =>
            {
                await onMessageAsync(new PrereserveQueueMessage(
                    "prereserve/queue",
                    "排队成功！请在2分钟内选择座位，否则需要重新排队。",
                    0,
                    0,
                    string.Empty), cancellationToken);
                await stopQueue.Task.WaitAsync(cancellationToken);
            }
        };
        var runtimeState = new AppRuntimeState
        {
            Session = new SessionCredentials("Authorization=a; SERVERID=b", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var coordinator = new TomorrowReservationCoordinator(
            apiClient,
            queueClient,
            new FakeTaskAlertService(),
            new ActivityLogService(),
            runtimeState);
        var plan = new TomorrowReservationPlan(
            117580,
            "自科阅览区",
            [new TrackedSeat("selected", "225")],
            GrabMode.Aggressive,
            new GrabSeatPollingStrategy(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
                50,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10)),
            null);

        await coordinator.StartAsync(plan);
        await fallbackSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.StopAsync();
        stopQueue.TrySetResult();

        Assert.Equal("selected", submittedSeatKeys[0]);
        Assert.Contains(submittedSeatKeys[1], new[] { "fallback-a", "fallback-b" });
        Assert.DoesNotContain("occupied-tomorrow", submittedSeatKeys);
        Assert.DoesNotContain(submittedSeatKeys.Skip(1), seatKey => seatKey == "selected");
    }

    [Fact]
    public async Task StartAsync_SubmitsAllRandomFallbackCandidates_InSameCycleUntilOneSucceeds()
    {
        var submittedSeatKeys = new List<string>();
        var successfulFallbackSubmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopQueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apiClient = new FakeTraceIntApiClient
        {
            OnRefreshPrereservePageAsync = (_, _) => Task.CompletedTask,
            OnGetPrereserveLibraryLayoutAsync = (_, _, _) => Task.FromResult(new LibraryLayout(
                117580,
                "自科阅览区",
                "3",
                true,
                6,
                1,
                0,
                [
                    new SeatSnapshot("selected", "225", false, 0, 0),
                    new SeatSnapshot("occupied-tomorrow", "226", true, 1, 0),
                    new SeatSnapshot("fallback-a", "227", false, 2, 0),
                    new SeatSnapshot("fallback-b", "228", false, 3, 0),
                    new SeatSnapshot("fallback-a", "227 duplicate", false, 4, 0)
                ])),
            OnSavePrereserveSeatAsync = (cookie, libraryId, seatKey, _) =>
            {
                submittedSeatKeys.Add(seatKey);
                if (seatKey == "selected")
                {
                    throw new TraceIntApiException("座位已被预约", 1, "座位已被预约");
                }

                if (submittedSeatKeys.Count(seenSeatKey => seenSeatKey is "fallback-a" or "fallback-b") == 1)
                {
                    throw new TraceIntApiException("座位已被预约", 1, "座位已被预约");
                }

                if (seatKey is "fallback-a" or "fallback-b")
                {
                    successfulFallbackSubmitted.TrySetResult();
                    return Task.FromResult(new PrereserveSaveResult(true, cookie));
                }

                return Task.FromResult(new PrereserveSaveResult(false, cookie));
            }
        };
        var queueClient = new FakePrereserveQueueClient
        {
            OnRunAsync = async (onMessageAsync, cancellationToken) =>
            {
                await onMessageAsync(new PrereserveQueueMessage(
                    "prereserve/queue",
                    "排队成功！请在2分钟内选择座位，否则需要重新排队。",
                    0,
                    0,
                    string.Empty), cancellationToken);
                await stopQueue.Task.WaitAsync(cancellationToken);
            }
        };
        var runtimeState = new AppRuntimeState
        {
            Session = new SessionCredentials("Authorization=a; SERVERID=b", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var coordinator = new TomorrowReservationCoordinator(
            apiClient,
            queueClient,
            new FakeTaskAlertService(),
            new ActivityLogService(),
            runtimeState);
        var plan = new TomorrowReservationPlan(
            117580,
            "自科阅览区",
            [new TrackedSeat("selected", "225")],
            GrabMode.Aggressive,
            new GrabSeatPollingStrategy(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
                50,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10)),
            null);

        await coordinator.StartAsync(plan);
        await successfulFallbackSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.StopAsync();
        stopQueue.TrySetResult();

        Assert.Equal("selected", submittedSeatKeys[0]);
        Assert.Contains("fallback-a", submittedSeatKeys);
        Assert.Contains("fallback-b", submittedSeatKeys);
        Assert.DoesNotContain("occupied-tomorrow", submittedSeatKeys);
        Assert.Equal(2, submittedSeatKeys.Count(seatKey => seatKey is "fallback-a" or "fallback-b"));
        Assert.Equal(1, submittedSeatKeys.Count(seatKey => seatKey == "fallback-a"));
    }

    [Fact]
    public async Task StartAsync_RequeuesAndContinues_WhenPrereserveRequiresQueueAgain()
    {
        var saveCalls = 0;
        var secondSubmitSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apiClient = new FakeTraceIntApiClient
        {
            OnRefreshPrereservePageAsync = (_, _) => Task.CompletedTask,
            OnSavePrereserveSeatAsync = (cookie, libraryId, seatKey, _) =>
            {
                saveCalls++;
                if (saveCalls == 1)
                {
                    throw new TraceIntApiException("请先排队再选座", 40006, "请先排队再选座");
                }

                secondSubmitSeen.TrySetResult();
                return Task.FromResult(new PrereserveSaveResult(true, cookie));
            }
        };
        var queueRuns = 0;
        var queueClient = new FakePrereserveQueueClient
        {
            OnRunAsync = async (onMessageAsync, cancellationToken) =>
            {
                queueRuns++;
                await onMessageAsync(new PrereserveQueueMessage(
                    "prereserve/queue",
                    "排队成功！请在2分钟内选择座位，否则需要重新排队。",
                    0,
                    0,
                    string.Empty), cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        var alerts = new FakeTaskAlertService();
        var coordinator = CreateCoordinator(apiClient, queueClient, alerts);

        await coordinator.StartAsync(CreatePlan());
        await secondSubmitSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.StopAsync();

        Assert.True(queueRuns >= 2);
        Assert.True(saveCalls >= 2);
        Assert.Empty(alerts.TaskFailedNotifications);
    }

    [Fact]
    public async Task StartAsync_KeepsMonitoring_WhenTomorrowVenueIsFull()
    {
        var secondCycleSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCalls = 0;
        var apiClient = new FakeTraceIntApiClient
        {
            OnRefreshPrereservePageAsync = (_, _) => Task.CompletedTask,
            OnGetPrereserveLibraryLayoutAsync = (_, _, _) => Task.FromResult(new LibraryLayout(
                117580,
                "自科阅览区",
                "3",
                true,
                2,
                2,
                0,
                [
                    new SeatSnapshot("selected", "225", true, 0, 0),
                    new SeatSnapshot("occupied", "226", true, 1, 0)
                ])),
            OnSavePrereserveSeatAsync = (_, _, _, _) =>
            {
                saveCalls++;
                if (saveCalls >= 2)
                {
                    secondCycleSeen.TrySetResult();
                }

                throw new TraceIntApiException("场馆已满，暂无空位", 1, "场馆已满，暂无空位");
            }
        };
        var queueClient = CreateReadyQueueClient();
        var alerts = new FakeTaskAlertService();
        var coordinator = CreateCoordinator(apiClient, queueClient, alerts);

        await coordinator.StartAsync(CreatePlan());
        await secondCycleSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.StopAsync();

        Assert.True(saveCalls >= 2);
        Assert.Empty(alerts.TaskFailedNotifications);
    }

    [Fact]
    public async Task StartAsync_RetriesLater_WhenServerIsBusy()
    {
        var secondSubmitSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCalls = 0;
        var apiClient = new FakeTraceIntApiClient
        {
            OnRefreshPrereservePageAsync = (_, _) => Task.CompletedTask,
            OnSavePrereserveSeatAsync = (cookie, _, _, _) =>
            {
                saveCalls++;
                if (saveCalls == 1)
                {
                    throw new TraceIntApiException("服务器繁忙，请稍后重试", 1, "服务器繁忙，请稍后重试");
                }

                secondSubmitSeen.TrySetResult();
                return Task.FromResult(new PrereserveSaveResult(true, cookie));
            }
        };
        var queueClient = CreateReadyQueueClient();
        var alerts = new FakeTaskAlertService();
        var coordinator = CreateCoordinator(apiClient, queueClient, alerts);

        await coordinator.StartAsync(CreatePlan());
        await secondSubmitSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.StopAsync();

        Assert.True(saveCalls >= 2);
        Assert.Empty(alerts.TaskFailedNotifications);
    }

    [Fact]
    public async Task StartAsync_CancelsQueueSession_WhenUnexpectedFailureStopsTask()
    {
        var queueCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apiClient = new FakeTraceIntApiClient
        {
            OnRefreshPrereservePageAsync = (_, _) => Task.CompletedTask,
            OnSavePrereserveSeatAsync = (_, _, _, _) => throw new InvalidOperationException("boom")
        };
        var queueClient = new FakePrereserveQueueClient
        {
            OnRunAsync = async (onMessageAsync, cancellationToken) =>
            {
                await onMessageAsync(new PrereserveQueueMessage(
                    "prereserve/queue",
                    "排队成功！请在2分钟内选择座位，否则需要重新排队。",
                    0,
                    0,
                    string.Empty), cancellationToken);

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    queueCancelled.TrySetResult();
                    throw;
                }
            }
        };
        var coordinator = CreateCoordinator(apiClient, queueClient, new FakeTaskAlertService());

        await coordinator.StartAsync(CreatePlan());
        await queueCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(CoordinatorTaskState.Failed, coordinator.GetStatus().State);
    }

    private static TomorrowReservationCoordinator CreateCoordinator(
        FakeTraceIntApiClient apiClient,
        FakePrereserveQueueClient queueClient,
        FakeTaskAlertService alerts)
    {
        var runtimeState = new AppRuntimeState
        {
            Session = new SessionCredentials("Authorization=a; SERVERID=b", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };

        return new TomorrowReservationCoordinator(
            apiClient,
            queueClient,
            alerts,
            new ActivityLogService(),
            runtimeState);
    }

    private static FakePrereserveQueueClient CreateReadyQueueClient()
    {
        return new FakePrereserveQueueClient
        {
            OnRunAsync = async (onMessageAsync, cancellationToken) =>
            {
                await onMessageAsync(new PrereserveQueueMessage(
                    "prereserve/queue",
                    "排队成功！请在2分钟内选择座位，否则需要重新排队。",
                    0,
                    0,
                    string.Empty), cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
    }

    private static TomorrowReservationPlan CreatePlan()
    {
        return new TomorrowReservationPlan(
            117580,
            "自科阅览区",
            [new TrackedSeat("selected", "225")],
            GrabMode.Aggressive,
            new GrabSeatPollingStrategy(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
                50,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10)),
            null);
    }
}
