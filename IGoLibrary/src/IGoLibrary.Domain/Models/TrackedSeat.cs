namespace IGoLibrary.Domain.Models;

public sealed record TrackedSeat(
    string SeatKey,
    string SeatName);

public sealed record CommonSeat(
    int LibraryId,
    string SeatKey,
    string SeatName);
