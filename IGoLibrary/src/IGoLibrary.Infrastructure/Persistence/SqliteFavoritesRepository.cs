using IGoLibrary.Application.Abstractions;
using IGoLibrary.Domain.Models;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace IGoLibrary.Infrastructure.Persistence;

public sealed class SqliteFavoritesRepository(SqliteConnectionFactory connectionFactory) : IFavoritesRepository
{
    public async Task<IReadOnlyList<TrackedSeat>> GetFavoritesAsync(int libraryId, CancellationToken cancellationToken = default)
    {
        var results = new List<TrackedSeat>();

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT SeatKey, SeatName FROM Favorites WHERE LibraryId = $libraryId ORDER BY SeatName;";
        command.Parameters.AddWithValue("$libraryId", libraryId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TrackedSeat(
                reader.GetString(0),
                reader.GetString(1)));
        }

        Trace.WriteLine($"[DataDiagnostic] Favorites loaded: database={AppDataPaths.DatabasePath}; libraryId={libraryId}; count={results.Count}; seats={string.Join(",", results.Select(x => x.SeatName))}");
        return results;
    }

    public async Task SaveFavoritesAsync(int libraryId, IReadOnlyList<TrackedSeat> seats, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // Remember local removals so a later remote import cannot undo the user's choice.
        var excludeCommand = connection.CreateCommand();
        excludeCommand.Transaction = transaction;
        excludeCommand.CommandText = "INSERT OR IGNORE INTO FavoriteExclusions SELECT LibraryId, SeatKey FROM Favorites WHERE LibraryId = $libraryId;";
        excludeCommand.Parameters.AddWithValue("$libraryId", libraryId);
        await excludeCommand.ExecuteNonQueryAsync(cancellationToken);

        var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM Favorites WHERE LibraryId = $libraryId;";
        deleteCommand.Parameters.AddWithValue("$libraryId", libraryId);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

        foreach (var seat in seats.DistinctBy(x => x.SeatKey))
        {
            var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                INSERT INTO Favorites(LibraryId, SeatKey, SeatName)
                VALUES($libraryId, $seatKey, $seatName);
                """;
            insertCommand.Parameters.AddWithValue("$libraryId", libraryId);
            insertCommand.Parameters.AddWithValue("$seatKey", seat.SeatKey);
            insertCommand.Parameters.AddWithValue("$seatName", seat.SeatName);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);

            var restoreCommand = connection.CreateCommand();
            restoreCommand.Transaction = transaction;
            restoreCommand.CommandText = "DELETE FROM FavoriteExclusions WHERE LibraryId = $libraryId AND SeatKey = $seatKey;";
            restoreCommand.Parameters.AddWithValue("$libraryId", libraryId);
            restoreCommand.Parameters.AddWithValue("$seatKey", seat.SeatKey);
            await restoreCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ImportFavoritesAsync(int libraryId, IReadOnlyList<TrackedSeat> seats, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var seat in seats.DistinctBy(x => x.SeatKey))
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO Favorites(LibraryId, SeatKey, SeatName)
                SELECT $libraryId, $key, $name
                WHERE NOT EXISTS (SELECT 1 FROM FavoriteExclusions WHERE LibraryId = $libraryId AND SeatKey = $key);
                """;
            command.Parameters.AddWithValue("$libraryId", libraryId);
            command.Parameters.AddWithValue("$key", seat.SeatKey);
            command.Parameters.AddWithValue("$name", seat.SeatName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
