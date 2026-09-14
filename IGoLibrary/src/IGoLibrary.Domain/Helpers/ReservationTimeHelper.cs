namespace IGoLibrary.Domain.Helpers;

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

    public static TimeSpan GetReReserveTriggerRemaining(DateTimeOffset expirationTime, DateTimeOffset now, TimeSpan leadTime)
    {
        var remaining = expirationTime - now - leadTime;
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

}
