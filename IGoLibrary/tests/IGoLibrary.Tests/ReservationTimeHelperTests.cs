using IGoLibrary.Domain.Helpers;

namespace IGoLibrary.Tests;

public sealed class ReservationTimeHelperTests
{
    [Fact]
    public void FromUnixSeconds_ReturnsLocalTime()
    {
        const long timestamp = 1_710_000_000;

        var actual = ReservationTimeHelper.FromUnixSeconds(timestamp);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime(), actual);
    }

    [Fact]
    public void ShouldReReserve_ReturnsTrue_WhenExpirationWithinSixtySeconds()
    {
        var now = DateTimeOffset.Now;
        var expiration = now.AddSeconds(45);

        var shouldReReserve = ReservationTimeHelper.ShouldReReserve(expiration, now);

        Assert.True(shouldReReserve);
    }

    [Fact]
    public void ShouldReReserve_ReturnsFalse_WhenExpirationStillFarAway()
    {
        var now = DateTimeOffset.Now;
        var expiration = now.AddSeconds(180);

        var shouldReReserve = ReservationTimeHelper.ShouldReReserve(expiration, now);

        Assert.False(shouldReReserve);
    }

    [Fact]
    public void ShouldReReserve_UsesProvidedLeadTime()
    {
        var now = DateTimeOffset.Now;
        var expiration = now.AddMinutes(5);

        var shouldReReserve = ReservationTimeHelper.ShouldReReserve(expiration, now, TimeSpan.FromMinutes(6));

        Assert.True(shouldReReserve);
    }

    [Fact]
    public void GetReReserveTriggerRemaining_ReturnsTimeUntilLeadWindow()
    {
        var now = DateTimeOffset.Now;
        var expiration = now.AddMinutes(30);

        var remaining = ReservationTimeHelper.GetReReserveTriggerRemaining(expiration, now, TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromMinutes(29), remaining);
    }

    [Fact]
    public void GetEffectiveReReserveLeadTime_ReservesMinimumExecutionBudget()
    {
        var effective = ReservationTimeHelper.GetEffectiveReReserveLeadTime(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(5), effective);
    }

    [Fact]
    public void GetReReserveTriggerRemaining_UsesExecutionBudgetWhenLeadTimeIsTooSmall()
    {
        var now = DateTimeOffset.Now;
        var expiration = now.AddSeconds(3);

        var remaining = ReservationTimeHelper.GetReReserveTriggerRemaining(expiration, now, TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, remaining);
        Assert.True(ReservationTimeHelper.ShouldReReserve(expiration, now, TimeSpan.FromSeconds(1)));
    }

}
