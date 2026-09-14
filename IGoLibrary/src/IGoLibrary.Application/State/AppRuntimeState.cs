using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.State;

public sealed class AppRuntimeState
{
    public SessionCredentials? Session { get; set; }

    private long _sessionGeneration;

    public long SessionGeneration => Interlocked.Read(ref _sessionGeneration);

    public void BeginSessionChange() => Interlocked.Increment(ref _sessionGeneration);

    public IReadOnlyList<LibrarySummary> Libraries { get; set; } = [];

    public LibrarySummary? BoundLibrary { get; set; }

    public LibraryLayout? CurrentLayout { get; set; }

    public ReservationInfo? CurrentReservation { get; set; }

    public IReadOnlyList<ReservationRecord> ReservationRecords { get; set; } = [];
}
