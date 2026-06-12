using IGoLibrary.Ex.Domain.Helpers;

namespace IGoLibrary.Ex.Tests;

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
    public void ShouldReReserve_UsesScheduledTimeBeforeLeadWindow()
    {
        var now = new DateTimeOffset(2026, 5, 24, 14, 30, 0, TimeSpan.FromHours(8));
        var expiration = now.AddHours(2);

        var shouldReReserve = ReservationTimeHelper.ShouldReReserve(
            expiration,
            now,
            TimeSpan.FromMinutes(1),
            new TimeOnly(14, 29, 59));

        Assert.True(shouldReReserve);
    }

    [Fact]
    public void GetReReserveTriggerRemaining_ChoosesEarlierScheduledTime()
    {
        var now = new DateTimeOffset(2026, 5, 24, 14, 0, 0, TimeSpan.FromHours(8));
        var expiration = now.AddHours(2);

        var remaining = ReservationTimeHelper.GetReReserveTriggerRemaining(
            expiration,
            now,
            TimeSpan.FromMinutes(1),
            new TimeOnly(14, 30, 0));

        Assert.Equal(TimeSpan.FromMinutes(30), remaining);
    }
}
