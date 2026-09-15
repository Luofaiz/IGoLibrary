using IGoLibrary.Domain.Enums;

namespace IGoLibrary.Domain.Models;

public sealed record TaskHistoryEntry(
    string Id, string TaskType, string Source, string Target,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string Outcome, string Message, bool IsFinished,
    string Result = "")
{
    public TaskHistoryEntry WithStatus(CoordinatorStatus status)
    {
        if (IsFinished) return this;
        var finished = status.State is CoordinatorTaskState.Completed or CoordinatorTaskState.Failed;
        var outcome = status.State switch
        {
            CoordinatorTaskState.Failed => "失败",
            CoordinatorTaskState.Completed when status.Message is "已成功预约到目标座位。" or "已成功预约明日目标座位。" => "成功",
            CoordinatorTaskState.Completed => "已停止",
            CoordinatorTaskState.Stopping => "停止中",
            CoordinatorTaskState.Starting => "准备启动",
            _ => "运行中"
        };
        if (!finished && outcome == Outcome) return this;
        return this with { Outcome = outcome, Message = status.Message, IsFinished = finished,
            EndedAt = finished ? status.LastUpdatedAt ?? DateTimeOffset.Now : null };
    }

    public string TypeText => TaskType switch
    {
        "GrabSeat" => "今日抢座",
        "TomorrowReservation" => "明日预约",
        "OccupySeat" => "占座守护",
        _ => TaskType
    };
    public string TimeText => $"开始 {StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 结束 {(EndedAt is null ? "—" : EndedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}";
}
