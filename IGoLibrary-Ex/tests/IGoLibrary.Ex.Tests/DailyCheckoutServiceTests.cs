using IGoLibrary.Ex.Application.Services;
using IGoLibrary.Ex.Domain.Enums;
using IGoLibrary.Ex.Domain.Models;

namespace IGoLibrary.Ex.Tests;

public sealed class DailyCheckoutServiceTests
{
    [Fact]
    public async Task RunAsync_ReleasesCheckedInSeat_AndVerifiesReservationIsGone()
    {
        var sessionService = new FakeSessionService
        {
            RestoreResult = new SessionCredentials("cookie", SessionSource.Restored, DateTimeOffset.Now, true)
        };
        var reservation = new ReservationInfo(
            "checkout-token",
            1,
            "电子阅览室",
            "seat-8",
            "8",
            DateTimeOffset.Now.AddHours(1),
            IsCheckedIn: true);
        var queryCalls = 0;
        string? submittedToken = null;
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetReservationInfoAsync = (_, _) =>
                Task.FromResult<ReservationInfo?>(++queryCalls == 1 ? reservation : null),
            OnCancelReservationAsync = (_, token, _) =>
            {
                submittedToken = token;
                return Task.FromResult(true);
            }
        };
        var service = new DailyCheckoutService(sessionService, apiClient, new ActivityLogService());

        var result = await service.RunAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.SeatReleased);
        Assert.Equal("checkout-token", submittedToken);
        Assert.Equal(2, queryCalls);
    }

    [Fact]
    public async Task RunAsync_ReturnsSuccessfulNoOp_WhenThereIsNoTodayReservation()
    {
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.Restored, DateTimeOffset.Now, true)
        };
        var cancelCalls = 0;
        var apiClient = new FakeTraceIntApiClient
        {
            OnCancelReservationAsync = (_, _, _) =>
            {
                cancelCalls++;
                return Task.FromResult(true);
            }
        };
        var service = new DailyCheckoutService(sessionService, apiClient, new ActivityLogService());

        var result = await service.RunAsync();

        Assert.True(result.Succeeded);
        Assert.False(result.SeatReleased);
        Assert.Equal(0, cancelCalls);
    }

    [Fact]
    public async Task RunAsync_FailsWithoutCallingApi_WhenStoredSessionCannotBeRestored()
    {
        var sessionService = new FakeSessionService();
        var reservationQueries = 0;
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetReservationInfoAsync = (_, _) =>
            {
                reservationQueries++;
                return Task.FromResult<ReservationInfo?>(null);
            }
        };
        var service = new DailyCheckoutService(sessionService, apiClient, new ActivityLogService());

        var result = await service.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(1, sessionService.RestoreCalls);
        Assert.Equal(0, reservationQueries);
    }
}
