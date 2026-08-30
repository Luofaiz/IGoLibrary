using IGoLibrary.Domain.Enums;

namespace IGoLibrary.Domain.Models;

public sealed record GrabSeatPlan(
    int LibraryId,
    string LibraryName,
    IReadOnlyList<TrackedSeat> Seats,
    GrabMode Mode,
    GrabSeatPollingStrategy PollingStrategy,
    TimeOnly? ScheduledStart,
    bool UseRandomAvailableSeat = false);
