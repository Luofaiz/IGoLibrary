using System.Diagnostics;
using System.Net.WebSockets;
using IGoLibrary.Application.Exceptions;
using IGoLibrary.Application.Services;
using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Tests;

public sealed partial class TomorrowReservationCoordinatorTests
{
    [Fact]
    public void BusyCooldown_IsBounded_UsesElapsedTime_AndResetsAfterNonBusyResult()
    {
        var clock = new SubmissionClock();
        var backoff = new TomorrowSubmissionBackoff(clock);
        Assert.Equal(TimeSpan.Zero, backoff.Remaining);
        Assert.Equal(TimeSpan.FromMilliseconds(300), backoff.RecordBusy());
        clock.Advance(200);
        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.Remaining);
        clock.Advance(200);
        Assert.Equal(TimeSpan.Zero, backoff.Remaining);
        Assert.Equal(TimeSpan.FromMilliseconds(600), backoff.RecordBusy());
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), backoff.RecordBusy());
        }
        backoff.Reset();
        Assert.Equal(TimeSpan.Zero, backoff.Remaining);
        Assert.Equal(TimeSpan.FromMilliseconds(300), backoff.RecordBusy());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyCooldown_AppliesAcrossRounds_InSelectedAndRandomModes(bool randomMode)
    {
        var times = new List<long>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeTraceIntApiClient
        {
            OnGetPrereserveLibraryLayoutAsync = (_, _, _) => Task.FromResult(new LibraryLayout(
                117580, "场馆", "3", true, 1, 1, 1, [new SeatSnapshot("selected", "225", false, 0, 0)])),
            OnSavePrereserveSeatAsync = async (cookie, _, _, token) =>
            {
                times.Add(Stopwatch.GetTimestamp());
                if (times.Count == 1)
                {
                    throw new TraceIntApiException("请重新尝试", 1);
                }
                second.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new PrereserveSaveResult(true, cookie);
            }
        };
        var coordinator = CreateCoordinator(api, CreateReadyQueueClient(), new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan() with { UseRandomAvailableSeat = randomMode });
            await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(Stopwatch.GetElapsedTime(times[0], times[1]) >= TimeSpan.FromMilliseconds(290));
        }
        finally { await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task BusyCooldown_IsSharedAcrossSeats_WithoutChangingPriority()
    {
        var submissions = new List<(string Key, long Time)>();
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeTraceIntApiClient
        {
            OnSavePrereserveSeatAsync = async (cookie, _, key, token) =>
            {
                submissions.Add((key, Stopwatch.GetTimestamp()));
                if (submissions.Count <= 2) throw new TraceIntApiException("系统繁忙，请重新尝试", 1);
                third.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new PrereserveSaveResult(true, cookie);
            }
        };
        var coordinator = CreateCoordinator(api, CreateReadyQueueClient(), new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan([new("first", "A"), new("second", "B"), new("third", "C")]));
            await third.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(["first", "second", "third"], submissions.Select(x => x.Key).ToArray());
            Assert.True(Stopwatch.GetElapsedTime(submissions[0].Time, submissions[1].Time) >= TimeSpan.FromMilliseconds(290));
            Assert.True(Stopwatch.GetElapsedTime(submissions[1].Time, submissions[2].Time) >= TimeSpan.FromMilliseconds(590));
        }
        finally { await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task Warmup_StillRuns_WhenQueueReadyArrivesAfterScheduledTime()
    {
        var calls = new List<string>();
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = DateTimeOffset.Now.AddMilliseconds(200);
        var api = new FakeTraceIntApiClient
        {
            OnWarmUpPrereserveLibraryAsync = (_, id, _) =>
            {
                Assert.Equal(117580, id);
                calls.Add("warmup");
                return Task.CompletedTask;
            },
            OnSavePrereserveSeatAsync = async (cookie, _, _, token) =>
            {
                calls.Add("save");
                submitted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new PrereserveSaveResult(true, cookie);
            }
        };
        var queue = new FakePrereserveQueueClient
        {
            OnRunAsync = async (callback, token) =>
            {
                await Task.Delay(400, token);
                await callback(ReadyMessage(), token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var coordinator = CreateCoordinator(api, queue, new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan(scheduledStart: TimeOnly.FromDateTime(target.LocalDateTime)));
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(["warmup", "save"], calls);
        }
        finally { await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task Warmup_HasShortDeadline_EvenIfDependencyIgnoresCancellation()
    {
        var pendingWarmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken warmupToken = default;
        var watch = Stopwatch.StartNew();
        var api = new FakeTraceIntApiClient
        {
            OnWarmUpPrereserveLibraryAsync = (_, _, token) =>
            {
                warmupToken = token;
                return pendingWarmup.Task;
            },
            OnSavePrereserveSeatAsync = async (cookie, _, _, token) =>
            {
                submitted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new PrereserveSaveResult(true, cookie);
            }
        };
        var coordinator = CreateCoordinator(api, CreateReadyQueueClient(), new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan());
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(warmupToken.IsCancellationRequested);
            Assert.InRange(watch.Elapsed.TotalMilliseconds, 700, 2000);
        }
        finally
        {
            pendingWarmup.TrySetException(new HttpRequestException("late warmup failure"));
            await coordinator.StopAsync();
        }
    }

    [Fact]
    public async Task RepeatedValidationMessages_DoNotBlockQueueReady_AndCancelOnStop()
    {
        var validates = 0;
        var validationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = async (_, token) =>
            {
                Interlocked.Increment(ref validates);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { validationCancelled.TrySetResult(); }
            },
            OnSavePrereserveSeatAsync = async (cookie, _, _, token) =>
            {
                submitted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new PrereserveSaveResult(true, cookie);
            }
        };
        var queue = new FakePrereserveQueueClient
        {
            OnRunAsync = async (callback, token) =>
            {
                for (var i = 0; i < 7; i++) await callback(new("prereserve/queue", "", 0, 1, ""), token);
                await callback(ReadyMessage(), token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var coordinator = CreateCoordinator(api, queue, new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan());
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, validates);
        }
        finally { await coordinator.StopAsync(); }
        await validationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect_RequeuesAndWarmsUp_BeforeNextSeat(bool faulted)
    {
        var firstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requeueStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueRuns = 0;
        var warmups = 0;
        var keys = new List<string>();
        var api = new FakeTraceIntApiClient
        {
            OnWarmUpPrereserveLibraryAsync = (_, _, _) => { warmups++; return Task.CompletedTask; },
            OnSavePrereserveSeatAsync = async (cookie, _, key, token) =>
            {
                keys.Add(key);
                if (keys.Count == 1)
                {
                    firstSave.TrySetResult();
                    throw new TraceIntApiException("请重新尝试", 1);
                }
                secondSave.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new PrereserveSaveResult(true, cookie);
            }
        };
        var queue = new FakePrereserveQueueClient
        {
            OnRunAsync = async (callback, token) =>
            {
                if (Interlocked.Increment(ref queueRuns) == 1)
                {
                    await callback(ReadyMessage(), token);
                    await firstSave.Task.WaitAsync(token);
                    if (faulted) throw new WebSocketException("connection lost");
                    return;
                }
                requeueStarted.TrySetResult();
                await allowReady.Task.WaitAsync(token);
                await callback(ReadyMessage(), token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var alerts = new FakeTaskAlertService();
        var coordinator = CreateCoordinator(api, queue, alerts);
        try
        {
            await coordinator.StartAsync(CreatePlan([new("first", "A"), new("second", "B")]));
            await requeueStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(350);
            Assert.Single(keys);
            allowReady.TrySetResult();
            await secondSave.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(["first", "second"], keys);
            Assert.Equal(2, warmups);
            Assert.Equal(2, queueRuns);
            Assert.Empty(alerts.TaskFailedNotifications);
        }
        finally { await coordinator.StopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticationFailure_InWarmupOrValidation_StopsWithoutSaving(bool duringValidation)
    {
        var saves = 0;
        var api = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = (_, _) => duringValidation
                ? Task.FromException(new TraceIntApiException("请重新登录", 1000, isAuthorizationDenied: true))
                : Task.CompletedTask,
            OnWarmUpPrereserveLibraryAsync = (_, _, _) => duringValidation
                ? Task.CompletedTask
                : Task.FromException(new TraceIntApiException("请重新登录", 1000, isAuthorizationDenied: true)),
            OnSavePrereserveSeatAsync = (cookie, _, _, _) =>
            {
                saves++;
                return Task.FromResult(new PrereserveSaveResult(true, cookie));
            }
        };
        var queue = new FakePrereserveQueueClient
        {
            OnRunAsync = async (callback, token) =>
            {
                await callback(duringValidation ? new("prereserve/queue", "", 0, 1, "") : ReadyMessage(), token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var coordinator = CreateCoordinator(api, queue, new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan());
            await WaitForAsync(() => coordinator.GetStatus().State == CoordinatorTaskState.Failed);
            Assert.Equal(0, saves);
        }
        finally { await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task SuccessDuringBusyCooldown_StopsWithoutAnotherSave()
    {
        var firstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = 0;
        var api = new FakeTraceIntApiClient
        {
            OnSavePrereserveSeatAsync = (_, _, _, _) =>
            {
                saves++;
                firstSave.TrySetResult();
                throw new TraceIntApiException("请重新尝试", 1);
            }
        };
        var queue = new FakePrereserveQueueClient
        {
            OnRunAsync = async (callback, token) =>
            {
                await callback(ReadyMessage(), token);
                await firstSave.Task.WaitAsync(token);
                await callback(new("prereserve/queue", "你已经成功登记了明天的225座位", 0, 0, ""), token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var coordinator = CreateCoordinator(api, queue, new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan([new("first", "A"), new("second", "B")]));
            await WaitForAsync(() => coordinator.GetStatus().State == CoordinatorTaskState.Completed);
            Assert.Equal(1, saves);
        }
        finally { await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task Recovery_RetriesTransientReconnectFailure_AndUsesNewQueueWarmup()
    {
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var warmups = 0;
        var api = new FakeTraceIntApiClient
        {
            OnWarmUpPrereserveLibraryAsync = (_, _, _) => { warmups++; return Task.CompletedTask; },
            OnSavePrereserveSeatAsync = async (cookie, _, _, token) =>
            {
                Assert.Equal(3, runs);
                submitted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new PrereserveSaveResult(true, cookie);
            }
        };
        var queue = new FakePrereserveQueueClient
        {
            OnRunAsync = (callback, token) =>
            {
                var run = Interlocked.Increment(ref runs);
                // Synchronous failure must also be observed by the queue supervisor.
                if (run == 2) throw new WebSocketException("temporary connect failure");
                return RunConnectionAsync();

                async Task RunConnectionAsync()
                {
                    await callback(ReadyMessage(), token);
                    if (run == 1) return;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            }
        };
        var coordinator = CreateCoordinator(api, queue, new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan());
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, warmups);
        }
        finally { await coordinator.StopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringWarmupOrBusyCooldown_DoesNotSubmitAnotherSeat(bool busyCooldown)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = 0;
        var api = new FakeTraceIntApiClient
        {
            OnWarmUpPrereserveLibraryAsync = async (_, _, token) =>
            {
                if (busyCooldown) return;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            OnSavePrereserveSeatAsync = (_, _, _, _) =>
            {
                saves++;
                entered.TrySetResult();
                throw new TraceIntApiException("请重新尝试", 1);
            }
        };
        var coordinator = CreateCoordinator(api, CreateReadyQueueClient(), new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan([new("first", "A"), new("second", "B")]));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(busyCooldown ? 1 : 0, saves);
            Assert.Equal(CoordinatorTaskState.Completed, coordinator.GetStatus().State);
        }
        finally { await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task QueueAuthenticationFailureAfterReady_IsNotRetriedAsDisconnect()
    {
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var saves = 0;
        var api = new FakeTraceIntApiClient
        {
            OnSavePrereserveSeatAsync = (_, _, _, _) =>
            {
                saves++;
                saveStarted.TrySetResult();
                throw new TraceIntApiException("请重新尝试", 1);
            }
        };
        var queue = new FakePrereserveQueueClient
        {
            OnRunAsync = async (callback, token) =>
            {
                runs++;
                await callback(ReadyMessage(), token);
                await saveStarted.Task.WaitAsync(token);
                await callback(new("prereserve/queue", "1000", 1000, 0, ""), token);
            }
        };
        var coordinator = CreateCoordinator(api, queue, new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan([new("first", "A"), new("second", "B")]));
            await WaitForAsync(() => coordinator.GetStatus().State == CoordinatorTaskState.Failed);
            Assert.Equal(1, saves);
            Assert.Equal(1, runs);
        }
        finally { await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task SuccessOnlyQueueMessage_CompletesWithoutWarmupOrSave()
    {
        var api = new FakeTraceIntApiClient
        {
            OnWarmUpPrereserveLibraryAsync = (_, _, _) => throw new InvalidOperationException("unexpected warmup"),
            OnSavePrereserveSeatAsync = (_, _, _, _) => throw new InvalidOperationException("unexpected save")
        };
        var queue = new FakePrereserveQueueClient
        {
            OnRunAsync = async (callback, token) =>
            {
                await callback(new("prereserve/queue", "你已经成功登记了明天的225座位", 0, 0, ""), token);
            }
        };
        var coordinator = CreateCoordinator(api, queue, new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan());
            await WaitForAsync(() => coordinator.GetStatus().State == CoordinatorTaskState.Completed);
            Assert.Contains("已成功预约", coordinator.GetStatus().Message);
        }
        finally { await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task ValidationTransientFailure_CanRetryWithoutReplayingEveryQueueMessage()
    {
        var validations = 0;
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = (_, _) => ++validations == 1
                ? Task.FromException(new HttpRequestException("temporary failure"))
                : Task.CompletedTask,
            OnSavePrereserveSeatAsync = async (cookie, _, _, token) =>
            {
                submitted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new PrereserveSaveResult(true, cookie);
            }
        };
        var queue = new FakePrereserveQueueClient
        {
            OnRunAsync = async (callback, token) =>
            {
                var refresh = new PrereserveQueueMessage("prereserve/queue", "", 0, 1, "");
                for (var i = 0; i < 7; i++) await callback(refresh, token);
                Assert.Equal(1, validations);
                await Task.Delay(2100, token);
                await callback(refresh, token);
                Assert.Equal(2, validations);
                for (var i = 0; i < 7; i++) await callback(refresh, token);
                await callback(ReadyMessage(), token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var coordinator = CreateCoordinator(api, queue, new FakeTaskAlertService());
        try
        {
            await coordinator.StartAsync(CreatePlan());
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(2, validations);
        }
        finally { await coordinator.StopAsync(); }
    }

    private static PrereserveQueueMessage ReadyMessage() => new("prereserve/queue", "排队成功", 0, 0, "");

    private sealed class SubmissionClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance(int milliseconds) => _timestamp += milliseconds;
    }
}
