namespace IGoLibrary.Domain.Models;

public sealed record GrabSeatSelection(int LibraryId, IReadOnlyList<string> SeatKeys);
