using CommunityToolkit.Mvvm.ComponentModel;
using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private string favoriteSyncStatusText = "尚未同步";
    [ObservableProperty] private string reservationRefreshStatusText = "尚未刷新";
    private bool _reservationRefreshPending;
    private DateTimeOffset? _todayRecordsUpdatedAt;
    private DateTimeOffset? _tomorrowRecordsUpdatedAt;

    private void ApplyReservationRefresh(ReservationRecordsRefresh result)
    {
        var now = DateTimeOffset.Now;
        if (result.TodayError is null) _todayRecordsUpdatedAt = now;
        if (result.TomorrowError is null) _tomorrowRecordsUpdatedAt = now;
        var retained = _reservationRecords.Where(x => x.Kind == ReservationRecordKind.Today
            ? result.TodayError is not null : result.TomorrowError is not null);
        UpdateReservationPresentation(retained.Concat(result.Records).ToArray());
        static string Describe(string name, DateTimeOffset? updated, Exception? error) =>
            $"{name}：{(error is null ? "已刷新" : updated is null ? "刷新失败，状态未知" : "刷新失败，保留旧记录")}";
        ReservationRefreshStatusText = Describe("今日", _todayRecordsUpdatedAt, result.TodayError) + "\n" +
            Describe("明日", _tomorrowRecordsUpdatedAt, result.TomorrowError);
    }

    private void ResetReservationRefreshState()
    {
        _todayRecordsUpdatedAt = _tomorrowRecordsUpdatedAt = null;
        _reservationRefreshPending = false;
        ReservationRefreshStatusText = "尚未刷新";
    }
}
