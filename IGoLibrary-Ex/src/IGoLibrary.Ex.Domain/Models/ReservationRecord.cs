using IGoLibrary.Ex.Domain.Enums;

namespace IGoLibrary.Ex.Domain.Models;

public sealed record ReservationRecord(
    ReservationRecordKind Kind,
    string ReservationToken,
    int LibraryId,
    string LibraryName,
    string SeatKey,
    string SeatName,
    DateTimeOffset? ExpirationTime,
    DateOnly? ReservationDate,
    bool IsUsed = false,
    bool IsCheckedIn = false)
{
    public bool CanCancel => Kind switch
    {
        ReservationRecordKind.Today => !IsCheckedIn && !string.IsNullOrWhiteSpace(ReservationToken),
        ReservationRecordKind.Tomorrow => !IsUsed,
        _ => false
    };
}
