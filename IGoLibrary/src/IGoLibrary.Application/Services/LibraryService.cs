using IGoLibrary.Application.Abstractions;
using IGoLibrary.Application.State;
using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Services;

public sealed class LibraryService(
    ITraceIntApiClient apiClient,
    IFavoritesRepository favoritesRepository,
    ISettingsService settingsService,
    IActivityLogService activityLogService,
    AppRuntimeState runtimeState) : ILibraryService
{
    private readonly SemaphoreSlim _bindingGate = new(1, 1);

    public LibrarySummary? BoundLibrary => runtimeState.BoundLibrary;

    public async Task<IReadOnlyList<LibrarySummary>> LoadLibrariesAsync(CancellationToken cancellationToken = default)
    {
        var generation = runtimeState.SessionGeneration;
        var cookie = runtimeState.Session?.Cookie ?? throw new InvalidOperationException("当前未登录。");
        var libraries = await apiClient.GetLibrariesAsync(cookie, cancellationToken);
        EnsureCurrentSession(generation);
        runtimeState.Libraries = libraries;
        activityLogService.Write(LogEntryKind.Success, "Library", $"已获取 {libraries.Count} 个可绑定场馆。");
        return libraries;
    }

    public async Task<LibraryLayout> BindLibraryAsync(int libraryId, CancellationToken cancellationToken = default)
    {
        await _bindingGate.WaitAsync(cancellationToken);
        try
        {
            var generation = runtimeState.SessionGeneration;
            var cookie = runtimeState.Session?.Cookie ?? throw new InvalidOperationException("当前未登录。");
            var libraries = runtimeState.Libraries.Count > 0
                ? runtimeState.Libraries
                : await apiClient.GetLibrariesAsync(cookie, cancellationToken);

            var target = libraries.FirstOrDefault(x => x.LibraryId == libraryId)
                ?? throw new InvalidOperationException("未找到指定场馆。");
            var layout = await apiClient.GetLibraryLayoutAsync(cookie, libraryId, cancellationToken);

            EnsureCurrentSession(generation);
            runtimeState.Libraries = libraries;
            runtimeState.BoundLibrary = target;
            runtimeState.CurrentLayout = layout;

            var settings = await settingsService.LoadAsync(cancellationToken);
            EnsureCurrentSession(generation);
            await settingsService.SaveAsync(settings with
            {
                LastLibraryId = target.LibraryId,
                LastLibraryName = target.Name
            }, cancellationToken);

            activityLogService.Write(LogEntryKind.Success, "Library", $"已绑定场馆：{target.Name}。");
            return layout;
        }
        finally
        {
            _bindingGate.Release();
        }
    }

    public async Task<LibraryLayout> RefreshBoundLibraryAsync(CancellationToken cancellationToken = default)
    {
        var generation = runtimeState.SessionGeneration;
        var cookie = runtimeState.Session?.Cookie ?? throw new InvalidOperationException("当前未登录。");
        var library = runtimeState.BoundLibrary ?? throw new InvalidOperationException("当前未绑定场馆。");
        var layout = await apiClient.GetLibraryLayoutAsync(cookie, library.LibraryId, cancellationToken);
        EnsureCurrentSession(generation);
        runtimeState.CurrentLayout = layout;
        return layout;
    }

    public Task<IReadOnlyList<TrackedSeat>> GetFavoritesAsync(int libraryId, CancellationToken cancellationToken = default)
    {
        return favoritesRepository.GetFavoritesAsync(libraryId, cancellationToken);
    }

    public async Task<IReadOnlyList<CommonSeat>> GetCommonSeatsAsync(CancellationToken cancellationToken = default)
    {
        var cookie = runtimeState.Session?.Cookie ?? throw new InvalidOperationException("当前未登录。");
        return await apiClient.GetCommonSeatsAsync(cookie, cancellationToken);
    }

    public async Task SaveFavoritesAsync(int libraryId, IReadOnlyList<TrackedSeat> seats, CancellationToken cancellationToken = default)
    {
        await favoritesRepository.SaveFavoritesAsync(libraryId, seats, cancellationToken);
        activityLogService.Write(LogEntryKind.Success, "Favorite", $"已保存 {seats.Count} 个收藏座位。");
    }

    private void EnsureCurrentSession(long generation)
    {
        if (generation != runtimeState.SessionGeneration || runtimeState.Session is null)
            throw new OperationCanceledException("登录状态已改变，已忽略旧场馆请求。");
    }

    public async Task<IReadOnlyList<TrackedSeat>> SyncFavoritesAsync(int libraryId, CancellationToken cancellationToken = default)
    {
        var generation = runtimeState.SessionGeneration;
        var remote = await GetCommonSeatsAsync(cancellationToken);
        EnsureCurrentSession(generation);
        await favoritesRepository.ImportFavoritesAsync(libraryId,
            remote.Where(x => x.LibraryId == libraryId && !string.IsNullOrWhiteSpace(x.SeatKey))
                .Select(x => new TrackedSeat(x.SeatKey, x.SeatName)).ToArray(), cancellationToken);
        return await GetFavoritesAsync(libraryId, cancellationToken);
    }
}
