using IGoLibrary.Desktop.Services;

namespace IGoLibrary.Tests;

public sealed class DailyCheckoutSchedulePolicyTests
{
    [Theory]
    [InlineData("21:21", 21, 21)]
    [InlineData("9:05", 9, 5)]
    public void TryParseTime_AcceptsConfiguredHourAndMinute(string value, int expectedHour, int expectedMinute)
    {
        Assert.True(DailyCheckoutSchedulePolicy.TryParseTime(value, out var checkoutTime));
        Assert.Equal(new TimeSpan(expectedHour, expectedMinute, 0), checkoutTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("25:00")]
    [InlineData("21:61")]
    public void TryParseTime_RejectsInvalidValues(string? value)
    {
        Assert.False(DailyCheckoutSchedulePolicy.TryParseTime(value, out _));
    }

    [Fact]
    public void IsWithinExecutionWindow_RejectsAStaleBootReplay()
    {
        var now = new DateTimeOffset(2026, 9, 5, 9, 25, 14, TimeSpan.FromHours(8));

        Assert.False(DailyCheckoutSchedulePolicy.IsWithinExecutionWindow(new TimeSpan(21, 21, 0), now));
    }

    [Fact]
    public void IsWithinExecutionWindow_AllowsNormalTaskDelay()
    {
        var now = new DateTimeOffset(2026, 9, 5, 21, 30, 0, TimeSpan.FromHours(8));

        Assert.True(DailyCheckoutSchedulePolicy.IsWithinExecutionWindow(new TimeSpan(21, 21, 0), now));
    }

    [Fact]
    public void IsWithinExecutionWindow_RejectsExcessiveDelay()
    {
        var now = new DateTimeOffset(2026, 9, 5, 21, 37, 0, TimeSpan.FromHours(8));

        Assert.False(DailyCheckoutSchedulePolicy.IsWithinExecutionWindow(new TimeSpan(21, 21, 0), now));
    }
}
