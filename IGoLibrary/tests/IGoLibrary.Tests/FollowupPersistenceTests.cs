using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;
using IGoLibrary.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace IGoLibrary.Tests;

public sealed class FollowupPersistenceTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("IGOLIBRARY_EX_DATA_DIR");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IGoLibrary-Tests", Guid.NewGuid().ToString("N"));
    public FollowupPersistenceTests() => Environment.SetEnvironmentVariable("IGOLIBRARY_EX_DATA_DIR", _directory);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("IGOLIBRARY_EX_DATA_DIR", _previous);
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task Favorites_RemovalSurvivesImportAndRestart_ExplicitSaveRestoresIt()
    {
        var factory = new SqliteConnectionFactory();
        await new SqliteAppDataInitializer(factory).InitializeAsync();
        var repository = new SqliteFavoritesRepository(factory);
        await repository.SaveFavoritesAsync(1, [new("local", "Local")]);
        await repository.ImportFavoritesAsync(1, [new("remote", "Remote"), new("remote", "Remote")]);
        Assert.Equal(2, (await repository.GetFavoritesAsync(1)).Count);
        await repository.SaveFavoritesAsync(1, [new("local", "Local")]);
        repository = new(factory);
        await repository.ImportFavoritesAsync(1, [new("remote", "Remote")]);
        Assert.Equal("local", Assert.Single(await repository.GetFavoritesAsync(1)).SeatKey);
        await repository.ImportFavoritesAsync(2, [new("remote", "Other venue")]);
        Assert.Single(await repository.GetFavoritesAsync(2));
        await repository.ImportFavoritesAsync(1, []);
        Assert.Single(await repository.GetFavoritesAsync(1));
        await repository.SaveFavoritesAsync(1, [new("local", "Local"), new("remote", "Remote")]);
        await repository.ImportFavoritesAsync(1, []);
        Assert.Equal(2, (await repository.GetFavoritesAsync(1)).Count);
    }

    [Theory]
    [InlineData(CoordinatorTaskState.Stopping, "正在停止", "停止中", false)]
    [InlineData(CoordinatorTaskState.Completed, "抢座任务已停止。", "已停止", true)]
    [InlineData(CoordinatorTaskState.Completed, "已签到，学习中，停止占座任务。", "已停止", true)]
    [InlineData(CoordinatorTaskState.Completed, "已成功预约明日目标座位。", "成功", true)]
    [InlineData(CoordinatorTaskState.Completed, "已成功预约到目标座位。", "成功", true)]
    [InlineData(CoordinatorTaskState.Failed, "会话失效", "失败", true)]
    public async Task History_StatusAndRestartPreserveTerminalOutcome(CoordinatorTaskState state, string message, string outcome, bool finished)
    {
        var factory = new SqliteConnectionFactory();
        await new SqliteAppDataInitializer(factory).InitializeAsync();
        var service = new SqliteTaskLaunchHistoryService(factory);
        var initial = new TaskHistoryEntry("run", "GrabSeat", "Desktop", "场馆 · A", DateTimeOffset.Now, null, "运行中", "", false);
        await service.SaveAsync(initial);
        var changed = initial.WithStatus(new(state, "", message, initial.StartedAt, DateTimeOffset.Now));
        Assert.Equal(outcome, changed.Outcome);
        Assert.Equal(finished, changed.IsFinished);
        Assert.Equal(finished, changed.EndedAt is not null);
        await service.SaveAsync(changed);
        if (finished) await service.SaveAsync(initial); // A late start write cannot overwrite the final result.
        service = new(factory);
        await service.MarkInterruptedAsync();
        var stored = Assert.Single(await service.GetRecentAsync());
        Assert.Equal(finished ? outcome : "已中断", stored.Outcome);
        Assert.True(stored.IsFinished);
    }
}
