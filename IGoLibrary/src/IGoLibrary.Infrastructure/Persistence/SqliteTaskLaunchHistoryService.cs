using IGoLibrary.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace IGoLibrary.Infrastructure.Persistence;

public sealed class SqliteTaskLaunchHistoryService(SqliteConnectionFactory connectionFactory) : ITaskLaunchHistoryService
{
    public async Task RecordAsync(string taskType, string source, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO TaskLaunchHistory(TaskType, Source, StartedAtUtc) VALUES ($type, $source, $started);";
        command.Parameters.AddWithValue("$type", taskType);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
