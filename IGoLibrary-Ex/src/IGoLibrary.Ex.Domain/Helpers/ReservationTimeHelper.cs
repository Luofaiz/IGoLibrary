namespace IGoLibrary.Ex.Domain.Helpers;

public static class ReservationTimeHelper
{
    public static readonly TimeSpan DefaultReReserveLeadTime = TimeSpan.FromSeconds(60);

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
        return expirationTime - now <= leadTime;
    }

    public static bool ShouldReReserve(
        DateTimeOffset expirationTime,
        DateTimeOffset now,
        TimeSpan leadTime,
        TimeOnly? scheduledReReserveTime)
    {
        if (ShouldReReserve(expirationTime, now, leadTime))
        {
            return true;
        }

        if (scheduledReReserveTime is null)
        {
            return false;
        }

        var scheduledAt = ResolveScheduledReReserveTime(expirationTime, now, scheduledReReserveTime.Value);
        return now >= scheduledAt;
    }

    public static TimeSpan GetReReserveTriggerRemaining(DateTimeOffset expirationTime, DateTimeOffset now, TimeSpan leadTime)
    {
        var remaining = expirationTime - now - leadTime;
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public static TimeSpan GetReReserveTriggerRemaining(
        DateTimeOffset expirationTime,
        DateTimeOffset now,
        TimeSpan leadTime,
        TimeOnly? scheduledReReserveTime)
    {
        var leadRemaining = GetReReserveTriggerRemaining(expirationTime, now, leadTime);
        if (scheduledReReserveTime is null)
        {
            return leadRemaining;
        }

        var scheduledAt = ResolveScheduledReReserveTime(expirationTime, now, scheduledReReserveTime.Value);
        var scheduledRemaining = scheduledAt - now;
        if (scheduledRemaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return scheduledRemaining <= leadRemaining ? scheduledRemaining : leadRemaining;
    }

    public static DateTimeOffset ResolveScheduledReReserveTime(
        DateTimeOffset expirationTime,
        DateTimeOffset now,
        TimeOnly scheduledReReserveTime)
    {
        var scheduledAt = new DateTimeOffset(
            now.Date.Add(scheduledReReserveTime.ToTimeSpan()),
            now.Offset);

        if (scheduledAt > expirationTime)
        {
            scheduledAt = scheduledAt.AddDays(-1);
        }

        if (now - scheduledAt >= TimeSpan.FromHours(12) && scheduledAt.AddDays(1) <= expirationTime)
        {
            scheduledAt = scheduledAt.AddDays(1);
        }

        return scheduledAt;
    }
}
