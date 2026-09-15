using IGoLibrary.Application.Abstractions;
using System.Text.Json;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Infrastructure.Persistence;

public sealed class SqliteTaskLaunchHistoryService(SqliteConnectionFactory connectionFactory) : ITaskLaunchHistoryService
{
    public async Task SaveAsync(TaskHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TaskExecutionHistory(Id, StartedAtUtc, IsFinished, Payload)
            VALUES ($id, $started, $finished, $payload)
            ON CONFLICT(Id) DO UPDATE SET IsFinished = excluded.IsFinished, Payload = excluded.Payload
            WHERE TaskExecutionHistory.IsFinished = 0;
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$started", entry.StartedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$finished", entry.IsFinished ? 1 : 0);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskHistoryEntry>> GetRecentAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM TaskExecutionHistory ORDER BY StartedAtUtc DESC LIMIT 100;";
        var entries = new List<TaskHistoryEntry>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(JsonSerializer.Deserialize<TaskHistoryEntry>(reader.GetString(0))!);
        command.CommandText = "SELECT Id, TaskType, Source, StartedAtUtc FROM TaskLaunchHistory ORDER BY Id DESC LIMIT 100;";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new($"legacy-{reader.GetInt64(0)}", reader.GetString(1), reader.GetString(2),
                    "旧版本未保存目标", DateTimeOffset.Parse(reader.GetString(3)), null,
                    "结果未知", "旧版仅记录启动请求，不能据此判断是否成功。", true));
        return entries.OrderByDescending(x => x.StartedAt).Take(100).ToArray();
    }

    public async Task MarkInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM TaskExecutionHistory WHERE IsFinished = 0;";
        var entries = new List<TaskHistoryEntry>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(JsonSerializer.Deserialize<TaskHistoryEntry>(reader.GetString(0))!);
        foreach (var entry in entries)
            await SaveAsync(entry with { IsFinished = true, Outcome = "已中断", Message = "上次程序退出前未记录最终结果，请刷新预约记录核实。" }, cancellationToken);
    }
}
