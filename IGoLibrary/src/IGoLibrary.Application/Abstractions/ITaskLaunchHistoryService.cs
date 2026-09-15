using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Abstractions;

public interface ITaskLaunchHistoryService
{
    Task SaveAsync(TaskHistoryEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskHistoryEntry>> GetRecentAsync(CancellationToken cancellationToken = default);
    Task MarkInterruptedAsync(CancellationToken cancellationToken = default);
}
