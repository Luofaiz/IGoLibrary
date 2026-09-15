using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly object _historyGate = new();
    private readonly Dictionary<string, TaskHistoryEntry> _activeTaskHistory = new(StringComparer.Ordinal);
    private Task _historyWrites = Task.CompletedTask;
    public ObservableCollection<TaskHistoryEntry> TaskHistory { get; } = [];
    [ObservableProperty] private string taskHistoryStatusText = "尚未加载";

    [RelayCommand]
    private async Task RefreshTaskHistoryAsync()
    {
        if (_taskLaunchHistoryService is null) return;
        try
        {
            Task writes;
            lock (_historyGate) writes = _historyWrites;
            await writes;
            var entries = await _taskLaunchHistoryService.GetRecentAsync();
            TaskHistory.Clear();
            foreach (var entry in entries) TaskHistory.Add(entry);
            TaskHistoryStatusText = entries.Count == 0 ? "暂无任务历史" : $"最近 {entries.Count} 条 · 本机记录";
        }
        catch (Exception ex) { TaskHistoryStatusText = $"读取历史失败：{ex.Message}"; }
    }

    private void QueueHistoryWrite(TaskHistoryEntry entry)
    {
        lock (_historyGate)
            _historyWrites = _historyWrites.ContinueWith(async _ =>
            {
                try { await _taskLaunchHistoryService!.SaveAsync(entry); }
                catch (Exception ex) { activityLogService.Write(LogEntryKind.Warning, "Task", $"记录任务历史失败：{ex.Message}"); }
            }, TaskScheduler.Default).Unwrap();
    }

    private async Task RecordAndStartAsync(string taskType, string source, Func<Task> start)
    {
        if (_taskLaunchHistoryService is null) { await start(); return; }
        var seats = string.Join(" → ", SelectedSeats.Select(x => x.SeatName));
        var target = taskType == "OccupySeat"
            ? $"{_currentReservation?.LibraryName} · {_currentReservation?.SeatName} · 到期前重约"
            : $"{SelectedLibrary?.Name} · {(string.IsNullOrEmpty(seats) ? "随机空座" : seats)} · {(taskType == "TomorrowReservation" ? "明日" : "今日")}";
        var entry = new TaskHistoryEntry(Guid.NewGuid().ToString("N"), taskType, source,
            target, DateTimeOffset.Now, null, "准备启动", "正在提交启动请求", false);
        lock (_historyGate)
        {
            if (_activeTaskHistory.ContainsKey(taskType)) throw new InvalidOperationException("任务仍在运行，请先停止。");
            _activeTaskHistory[taskType] = entry;
            QueueHistoryWrite(entry);
        }
        try
        {
            await start();
            var status = taskType switch
            {
                "GrabSeat" => grabSeatCoordinator.GetStatus(),
                "TomorrowReservation" => tomorrowReservationCoordinator.GetStatus(),
                _ => occupySeatCoordinator.GetStatus()
            };
            SaveTaskHistoryStatus(taskType, status);
        }
        catch (Exception ex)
        {
            lock (_historyGate)
            {
                if (_activeTaskHistory.Remove(taskType, out var failed))
                    QueueHistoryWrite(failed with { EndedAt = DateTimeOffset.Now, Outcome = "启动失败", Message = ex.Message, IsFinished = true });
            }
            throw;
        }
        finally { await RefreshTaskHistoryAsync(); }
    }

    private void SaveTaskHistoryStatus(string taskType, CoordinatorStatus status)
    {
        if (_taskLaunchHistoryService is null) return;
        lock (_historyGate)
        {
            if (!_activeTaskHistory.TryGetValue(taskType, out var entry)) return;
            var updated = entry.WithStatus(status);
            if (updated == entry) return;
            if (updated.IsFinished) _activeTaskHistory.Remove(taskType);
            else _activeTaskHistory[taskType] = updated;
            QueueHistoryWrite(updated);
        }
        Dispatcher.UIThread.Post(async () => await RefreshTaskHistoryAsync());
    }
}
