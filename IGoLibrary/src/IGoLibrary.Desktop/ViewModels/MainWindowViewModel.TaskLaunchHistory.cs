using IGoLibrary.Domain.Enums;

namespace IGoLibrary.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private async Task RecordAndStartAsync(string taskType, string source, Func<Task> start)
    {
        if (_taskLaunchHistoryService is not null)
        {
            try { await _taskLaunchHistoryService.RecordAsync(taskType, source); }
            catch (Exception ex) { activityLogService.Write(LogEntryKind.Warning, "Task", $"记录任务启动历史失败：{ex.Message}"); }
        }

        await start();
    }
}
