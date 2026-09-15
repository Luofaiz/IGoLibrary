using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Abstractions;

public interface IFavoritesRepository
{
    Task<IReadOnlyList<TrackedSeat>> GetFavoritesAsync(int libraryId, CancellationToken cancellationToken = default);

    Task SaveFavoritesAsync(int libraryId, IReadOnlyList<TrackedSeat> seats, CancellationToken cancellationToken = default);

    async Task ImportFavoritesAsync(int libraryId, IReadOnlyList<TrackedSeat> seats, CancellationToken cancellationToken = default)
    {
        var existing = await GetFavoritesAsync(libraryId, cancellationToken);
        await SaveFavoritesAsync(libraryId, existing.Concat(seats).DistinctBy(x => x.SeatKey).ToArray(), cancellationToken);
    }
}
