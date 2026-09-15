namespace IGoLibrary.Domain.Models;

public sealed record ReservationRecordsRefresh(
    IReadOnlyList<ReservationRecord> Records,
    Exception? TodayError = null,
    Exception? TomorrowError = null);
