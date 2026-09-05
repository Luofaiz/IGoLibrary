using System.Globalization;

namespace IGoLibrary.Desktop.Services;

internal static class DailyCheckoutSchedulePolicy
{
    internal static readonly TimeSpan EarlyStartTolerance = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan LateStartTolerance = TimeSpan.FromMinutes(15);

    public static bool TryParseTime(string? value, out TimeSpan checkoutTime)
    {
        checkoutTime = default;
        return TimeSpan.TryParseExact(
                   value?.Trim(),
                   [@"hh\:mm", @"h\:mm"],
                   CultureInfo.InvariantCulture,
                   out checkoutTime) &&
               checkoutTime >= TimeSpan.Zero &&
               checkoutTime < TimeSpan.FromDays(1);
    }

    public static bool IsWithinExecutionWindow(TimeSpan checkoutTime, DateTimeOffset now)
    {
        if (checkoutTime < TimeSpan.Zero || checkoutTime >= TimeSpan.FromDays(1))
        {
            return false;
        }

        var scheduledAt = new DateTimeOffset(now.Date.Add(checkoutTime), now.Offset);
        return now >= scheduledAt - EarlyStartTolerance &&
               now <= scheduledAt + LateStartTolerance;
    }
}
