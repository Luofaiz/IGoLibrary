using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using IGoLibrary.Ex.Desktop.Services;
using IGoLibrary.Ex.Domain.Enums;
using IGoLibrary.Ex.Domain.Models;

namespace IGoLibrary.Ex.Desktop.ViewModels;

public sealed partial class ReservationRecordViewModel(
    ReservationRecord record,
    DateTimeOffset now,
    IAppThemeService themeService) : ObservableObject
{
    public ReservationRecord Record { get; } = record;

    public bool CanCancel => Record.CanCancel;

    public string SeatNumberText => ExtractSeatNumberText(Record.SeatName);

    public string SeatNameText => string.IsNullOrWhiteSpace(Record.SeatName) ? "未知座位" : Record.SeatName;

    public string VenueText => string.IsNullOrWhiteSpace(Record.LibraryName) ? "未知场馆" : Record.LibraryName;

    public string KindText => Record.Kind == ReservationRecordKind.Today ? "今日预约" : "明日预约";

    public string TimeLabelText => Record.Kind == ReservationRecordKind.Today ? "当前有效至" : "预约日期";

    public string TimeValueText => Record.Kind == ReservationRecordKind.Today
        ? Record.ExpirationTime?.ToString("HH:mm:ss") ?? "--:--:--"
        : Record.ReservationDate?.ToString("yyyy-MM-dd") ?? "明日";

    public string RemainingLabelText => Record.IsCheckedIn || Record.Kind == ReservationRecordKind.Tomorrow ? "状态" : "剩余时间";

    public string RemainingText
    {
        get
        {
            if (Record.Kind == ReservationRecordKind.Tomorrow)
            {
                return Record.IsUsed ? "已使用" : "待生效";
            }

            if (Record.IsCheckedIn)
            {
                return "学习中";
            }

            if (Record.ExpirationTime is null)
            {
                return "--";
            }

            var remaining = Record.ExpirationTime.Value - now;
            return remaining <= TimeSpan.Zero ? "已到期" : FormatRemaining(remaining);
        }
    }

    public string BadgeText
    {
        get
        {
            if (Record.Kind == ReservationRecordKind.Tomorrow)
            {
                return Record.IsUsed ? "已使用" : "明日生效";
            }

            if (Record.IsCheckedIn)
            {
                return "学习中";
            }

            if (Record.ExpirationTime is null)
            {
                return "待刷新";
            }

            return Record.ExpirationTime.Value <= now ? "待刷新" : "生效中";
        }
    }

    public IBrush BadgeBrush
    {
        get
        {
            var palette = themeService.CurrentPalette;
            if (Record.Kind == ReservationRecordKind.Tomorrow)
            {
                return palette.RunningBrush;
            }

            if (Record.IsCheckedIn)
            {
                return palette.RunningBrush;
            }

            return Record.ExpirationTime is not null && Record.ExpirationTime.Value > now
                ? palette.SuccessBrush
                : palette.WarningBrush;
        }
    }

    public IBrush BadgeBackgroundBrush
    {
        get
        {
            var palette = themeService.CurrentPalette;
            if (Record.Kind == ReservationRecordKind.Tomorrow)
            {
                return palette.RunningSoftBrush;
            }

            if (Record.IsCheckedIn)
            {
                return palette.RunningSoftBrush;
            }

            return Record.ExpirationTime is not null && Record.ExpirationTime.Value > now
                ? palette.SuccessSoftBrush
                : palette.WarningSoftBrush;
        }
    }

    private static string ExtractSeatNumberText(string seatName)
    {
        var digits = new string(seatName.Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? seatName : digits;
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分";
        }

        if (remaining.TotalMinutes >= 1)
        {
            return $"{remaining.Minutes} 分 {remaining.Seconds} 秒";
        }

        return $"{Math.Max(0, remaining.Seconds)} 秒";
    }
}
