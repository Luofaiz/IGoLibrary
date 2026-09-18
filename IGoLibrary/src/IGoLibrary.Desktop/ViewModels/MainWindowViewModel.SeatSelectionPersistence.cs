using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    internal Task SeatSelectionSaveTask { get; private set; } = Task.CompletedTask;

    private void SaveGrabSeatSelection()
    {
        if (_isSigningOut || _seatLibraryId is not int libraryId) return;
        var snapshot = new GrabSeatSelection(libraryId, _committedSelectedSeatKeys.ToArray());
        SeatSelectionSaveTask = SaveGrabSeatSelectionAsync(SeatSelectionSaveTask, snapshot);
    }

    private async Task SaveGrabSeatSelectionAsync(Task previous, GrabSeatSelection snapshot)
    {
        await previous;
        try
        {
            var settings = await settingsService.LoadAsync();
            await settingsService.SaveAsync(settings with { LastGrabSeatSelection = snapshot });
        }
        catch (Exception ex)
        {
            activityLogService.Write(LogEntryKind.Warning, "Grab", $"保存上次目标座位失败：{ex.Message}");
        }
    }

    private async Task RestoreGrabSelectionAfterAuthorizationAsync(bool restorePreferredSelection = false)
    {
        if (IsGrabTaskActive || _isSigningOut) return;
        await SeatSelectionSaveTask;
        var settings = await settingsService.LoadAsync();
        var saved = settings.LastGrabSeatSelection;
        await LoadLibrariesAsync(restorePreferredSelection, saved?.LibraryId);
        if (SelectedLibrary is null || IsGrabTaskActive) return;
        await BindSelectedLibraryCoreAsync();
        if (saved is null || _seatLibraryId != saved.LibraryId || SelectedLibrary?.LibraryId != saved.LibraryId) return;

        var availableKeys = _allSeats.Select(seat => seat.SeatKey).ToHashSet(StringComparer.Ordinal);
        _committedSelectedSeatKeys.Clear();
        _committedSelectedSeatKeys.AddRange((saved.SeatKeys ?? [])
            .Where(availableKeys.Contains).Distinct(StringComparer.Ordinal));
        _draftSelectedSeatKeys.Clear();
        IsGrabSeatSelectionOverlayOpen = false;
        ApplySelectionToSeatItems(_committedSelectedSeatKeys);
        RefreshSelectedSeatsPresentation();
        UpdateDraftSelectionPresentation();
    }
}
