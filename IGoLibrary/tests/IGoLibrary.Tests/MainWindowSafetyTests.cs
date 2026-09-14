using IGoLibrary.Application.Services;
using IGoLibrary.Application.State;
using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Tests;

public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task TomorrowStop_WaitsForQueueCleanupEvenAfterSuccessStatus()
    {
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<PrereserveQueueMessage, CancellationToken, Task>? send = null;
        var queue = new FakePrereserveQueueClient
        {
            OnRunAsync = async (callback, token) =>
            {
                send = callback;
                await callback(new("prereserve/queue", "排队成功", 0, 0, ""), token);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException)
                {
                    cleanupEntered.SetResult();
                    await releaseCleanup.Task;
                }
            }
        };
        var api = new FakeTraceIntApiClient
        {
            OnSavePrereserveSeatAsync = async (cookie, _, _, token) =>
            {
                await send!(new("prereserve/queue", "你已经成功登记了明天的A座位", 0, 0, ""), token);
                return new(true, cookie);
            }
        };
        var coordinator = new TomorrowReservationCoordinator(api, queue, new FakeTaskAlertService(), new ActivityLogService(),
            new AppRuntimeState { Session = new("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true) });
        await coordinator.StartAsync(new(1, "One", [new("A", "A")], GrabMode.Aggressive,
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, TimeSpan.Zero, TimeSpan.Zero), null));
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = coordinator.StopAsync();
        Assert.False(stop.IsCompleted);
        releaseCleanup.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CoordinatorTaskState.Completed, coordinator.GetStatus().State);
    }

    [Fact]
    public async Task PreviewDifferentVenue_DoesNotStartOrWriteFavoritesForOldSeats()
    {
        var first = new LibrarySummary(1, "One", "1", true);
        var second = new LibrarySummary(2, "Two", "2", true);
        var libraries = new FakeLibraryService { LibrariesToLoad = [first, second] };
        libraries.LayoutsByLibraryId[1] = new(1, "One", "1", true, 1, 0, 0, [new("A", "A", false, 0, 0)]);
        var grab = new FakeGrabSeatCoordinator();
        var tomorrow = new FakeTomorrowReservationCoordinator();
        var vm = CreateViewModel(libraryService: libraries, grabSeatCoordinator: grab, tomorrowReservationCoordinator: tomorrow);
        vm.SelectedLibrary = first;
        await vm.BindSelectedLibraryCommand.ExecuteAsync(null);
        vm.SelectedSeats.Add(new("A", "A"));
        vm.SelectedLibrary = second;
        await vm.StartGrabCommand.ExecuteAsync(null);
        await vm.StartRandomAvailableSeatGrabCommand.ExecuteAsync(null);
        vm.SelectedGrabTaskTargetIndex = 1;
        await vm.StartGrabCommand.ExecuteAsync(null);
        await vm.SaveFavoritesCommand.ExecuteAsync(null);
        await vm.RemoveFavoritesCommand.ExecuteAsync(null);
        Assert.Equal(0, grab.StartCalls);
        Assert.Equal(0, tomorrow.StartCalls);
        Assert.Equal(0, libraries.SaveFavoritesCalls);
        Assert.False(vm.CanStartRandomAvailableSeatGrab);
    }

    [Fact]
    public async Task SignOut_WaitsForAllCoordinatorsBeforeClearingSession()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSessionService { CurrentSession = new("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true) };
        var grab = new FakeGrabSeatCoordinator { StopCompletion = release.Task };
        var tomorrow = new FakeTomorrowReservationCoordinator { StopCompletion = release.Task };
        var occupy = new FakeOccupySeatCoordinator { StopCompletion = release.Task };
        var vm = CreateViewModel(sessionService: session, grabSeatCoordinator: grab, tomorrowReservationCoordinator: tomorrow, occupySeatCoordinator: occupy);
        var signingOut = vm.SignOutCommand.ExecuteAsync(null);
        Assert.Equal(1, grab.StopCalls);
        Assert.Equal(1, tomorrow.StopCalls);
        Assert.Equal(1, occupy.StopCalls);
        Assert.Equal(0, session.SignOutCalls);
        Assert.False(vm.CanEditGrabConfiguration);
        var queuedStart = vm.StartOccupyCommand.ExecuteAsync(null);
        release.SetResult();
        await Task.WhenAll(signingOut, queuedStart);
        Assert.Equal(0, occupy.StartCalls);
        Assert.Null(session.CurrentSession);
        Assert.Empty(vm.SelectedSeats);
        Assert.False(vm.IsAuthorized);
    }

    [Fact]
    public async Task RefreshCompletingAfterSignOut_DoesNotRestoreReservationPresentation()
    {
        var result = new TaskCompletionSource<IReadOnlyList<ReservationRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSessionService { CurrentSession = new("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true) };
        var api = new FakeTraceIntApiClient { OnGetReservationRecordsAsync = (_, _) => result.Task };
        var vm = CreateViewModel(sessionService: session, apiClient: api);
        var refresh = vm.RefreshReservationCommand.ExecuteAsync(null);
        await vm.SignOutCommand.ExecuteAsync(null);
        result.SetResult([new(ReservationRecordKind.Today, "token", 1, "One", "A", "A", DateTimeOffset.Now.AddHours(1), DateOnly.FromDateTime(DateTime.Today))]);
        await refresh;
        Assert.False(vm.HasReservationRecords);
        Assert.False(vm.HasCurrentReservation);
    }

    [Fact]
    public async Task BindingCompletingAfterSignOut_DoesNotRestoreRuntimeVenue()
    {
        var result = new TaskCompletionSource<LibraryLayout>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new AppRuntimeState { Session = new("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true), Libraries = [new(1, "One", "1", true)] };
        var api = new FakeTraceIntApiClient { OnGetLibraryLayoutAsync = (_, _, _) => result.Task };
        var service = new LibraryService(api, new NoFavorites(), new FakeSettingsService(AppSettings.Default), new ActivityLogService(), runtime);
        var binding = service.BindLibraryAsync(1);
        await new SessionService(api, new FakeCredentialStore(), new ActivityLogService(), runtime).SignOutAsync();
        result.SetResult(new(1, "One", "1", true, 1, 0, 0, []));
        await Assert.ThrowsAsync<OperationCanceledException>(() => binding);
        Assert.Null(runtime.BoundLibrary);
        Assert.Null(runtime.CurrentLayout);
        Assert.Empty(runtime.Libraries);
    }

    private sealed class NoFavorites : IGoLibrary.Application.Abstractions.IFavoritesRepository
    {
        public Task<IReadOnlyList<TrackedSeat>> GetFavoritesAsync(int libraryId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TrackedSeat>>([]);
        public Task SaveFavoritesAsync(int libraryId, IReadOnlyList<TrackedSeat> seats, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
