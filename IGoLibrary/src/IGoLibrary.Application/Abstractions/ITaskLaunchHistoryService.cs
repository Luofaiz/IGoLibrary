namespace IGoLibrary.Application.Abstractions;

public interface ITaskLaunchHistoryService
{
    Task RecordAsync(string taskType, string source, CancellationToken cancellationToken = default);
}
