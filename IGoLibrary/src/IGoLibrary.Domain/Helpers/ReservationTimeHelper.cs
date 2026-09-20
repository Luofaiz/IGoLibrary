namespace IGoLibrary.Domain.Helpers;

public static class ReservationTimeHelper
{
    public static readonly TimeSpan DefaultReReserveLeadTime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinimumReReserveExecutionBudget = TimeSpan.FromSeconds(5);

    public static DateTimeOffset FromUnixSeconds(long timestamp)
    {
        return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime();
    }

    public static bool ShouldReReserve(DateTimeOffset expirationTime, DateTimeOffset now)
    {
        return ShouldReReserve(expirationTime, now, DefaultReReserveLeadTime);
    }

    public static bool ShouldReReserve(DateTimeOffset expirationTime, DateTimeOffset now, TimeSpan leadTime)
    {
        return expirationTime - now <= GetEffectiveReReserveLeadTime(leadTime);
    }

    public static TimeSpan GetEffectiveReReserveLeadTime(TimeSpan leadTime)
    {
        return leadTime < MinimumReReserveExecutionBudget
            ? MinimumReReserveExecutionBudget
            : leadTime;
    }

    public static TimeSpan GetReReserveTriggerRemaining(DateTimeOffset expirationTime, DateTimeOffset now, TimeSpan leadTime)
    {
        var remaining = expirationTime - now - GetEffectiveReReserveLeadTime(leadTime);
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

}
