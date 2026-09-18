using System.Text.Json;
using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Tests;

public sealed partial class MainWindowViewModelTests
{
    [Theory]
    [InlineData("login")]
    [InlineData("restore")]
    [InlineData("startup")]
    [InlineData("link")]
    public async Task Authorization_RestoresSavedSeatOrder_WithoutStartingTask(string entry)
    {
        var (library, libraries) = SelectionLibraries();
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            LastGrabSeatSelection = new(library.LibraryId, ["third", "missing", "first", "third"])
        });
        var session = new FakeSessionService { RestoreResult = new("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true) };
        var grab = new FakeGrabSeatCoordinator();
        var tomorrow = new FakeTomorrowReservationCoordinator();
        var vm = CreateViewModel(libraryService: libraries, settingsService: settings, sessionService: session,
            grabSeatCoordinator: grab, tomorrowReservationCoordinator: tomorrow,
            apiClient: new FakeTraceIntApiClient { OnGetCookieFromCodeAsync = (_, _) => Task.FromResult("cookie") });
        if (entry == "startup") await vm.InitializeAsync();
        else if (entry == "restore") await vm.RestoreSessionCommand.ExecuteAsync(null);
        else if (entry == "link")
        {
            await vm.TryAutoParseClipboardLinkAsync("https://wechat.v2.traceint.com/index.php/urlNew/auth.html?code=0123456789abcdef0123456789abcdef&state=1");
        }
        else
        {
            vm.ManualCookieText = "cookie";
            await vm.ValidateManualCookieCommand.ExecuteAsync(null);
        }
        Assert.Equal(library.LibraryId, vm.SelectedLibrary?.LibraryId);
        Assert.Equal(library.LibraryId, libraries.BoundLibrary?.LibraryId);
        Assert.True(vm.IsCurrentLocked);
        Assert.Equal(["third", "first"], vm.SelectedSeats.Select(x => x.SeatKey));
        Assert.Null(grab.StartedPlan);
        Assert.Null(tomorrow.StartedPlan);
        Assert.Contains(vm.VisibleSeats, x => x.SeatKey == "third" && x.IsSelected);
    }

    [Fact]
    public async Task CommittedSelection_PersistsOrderAcrossLogout_CancelledDraftDoesNotReplaceIt()
    {
        var (library, libraries) = SelectionLibraries();
        var settings = new FakeSettingsService(AppSettings.Default);
        var vm = CreateViewModel(libraryService: libraries, settingsService: settings);
        vm.IsAuthorized = true;
        vm.SelectedLibrary = library;
        await vm.BindSelectedLibraryCommand.ExecuteAsync(null);
        await vm.OpenGrabSeatSelectionOverlayCommand.ExecuteAsync(null);
        foreach (var seat in vm.VisibleSeats) seat.IsSelected = true;
        vm.MoveDraftSelectedSeatUpCommand.Execute(vm.DraftSelectedSeats[2]);
        vm.MoveDraftSelectedSeatUpCommand.Execute(vm.DraftSelectedSeats[1]);
        vm.ConfirmGrabSeatSelectionCommand.Execute(null);
        await vm.SeatSelectionSaveTask;
        Assert.Equal(["third", "first", "second"], settings.CurrentSettings.LastGrabSeatSelection!.SeatKeys);

        await vm.OpenGrabSeatSelectionOverlayCommand.ExecuteAsync(null);
        vm.RemoveDraftSelectedSeatCommand.Execute(vm.DraftSelectedSeats[0]);
        vm.CancelGrabSeatSelectionCommand.Execute(null);
        await vm.SignOutCommand.ExecuteAsync(null);
        Assert.Equal(["third", "first", "second"], settings.CurrentSettings.LastGrabSeatSelection!.SeatKeys);

        vm.ManualCookieText = "new-cookie";
        await vm.ValidateManualCookieCommand.ExecuteAsync(null);
        Assert.Equal(["third", "first", "second"], vm.SelectedSeats.Select(x => x.SeatKey));
        vm.MoveSelectedSeatDownCommand.Execute(vm.SelectedSeats[0]);
        vm.RemoveSelectedSeatCommand.Execute(vm.SelectedSeats[2]);
        await vm.SeatSelectionSaveTask;
        Assert.Equal(["first", "third"], settings.CurrentSettings.LastGrabSeatSelection!.SeatKeys);
        vm.ClearSelectedSeatsCommand.Execute(null);
        await vm.SeatSelectionSaveTask;
        Assert.Empty(settings.CurrentSettings.LastGrabSeatSelection!.SeatKeys);
        await vm.ValidateManualCookieCommand.ExecuteAsync(null);
        Assert.Empty(vm.SelectedSeats);
    }

    [Fact]
    public async Task MissingSavedVenue_DoesNotApplyKeysToAnotherVenue()
    {
        var (_, libraries) = SelectionLibraries();
        var settings = new FakeSettingsService(AppSettings.Default with { LastGrabSeatSelection = new(999, ["first"]) });
        var vm = CreateViewModel(libraryService: libraries, settingsService: settings);
        vm.ManualCookieText = "cookie";
        await vm.ValidateManualCookieCommand.ExecuteAsync(null);
        Assert.Null(vm.SelectedLibrary);
        Assert.Empty(vm.SelectedSeats);
        Assert.Equal(0, libraries.BindLibraryCalls);
    }

    [Fact]
    public void SelectionSettings_RoundTripOrderedKeys_AndOldSettingsRemainCompatible()
    {
        var settings = AppSettings.Default with { LastGrabSeatSelection = new(10, ["third", "first"]) };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(["third", "first"], restored.LastGrabSeatSelection!.SeatKeys);
        var legacy = JsonSerializer.SerializeToNode(AppSettings.Default)!.AsObject();
        legacy.Remove(nameof(AppSettings.LastGrabSeatSelection));
        Assert.Null(legacy.Deserialize<AppSettings>()!.LastGrabSeatSelection);
    }

    [Fact]
    public async Task SwitchingAndLockingVenue_ReplacesRememberedVenue_EvenBeforeSelectingSeats()
    {
        var (first, libraries) = SelectionLibraries();
        var second = first with { LibraryId = 20, Name = "新场馆" };
        libraries.LibrariesToLoad = [first, second];
        libraries.LayoutsByLibraryId[20] = libraries.LayoutsByLibraryId[10] with { LibraryId = 20, Name = "新场馆" };
        var settings = new FakeSettingsService(AppSettings.Default with { LastGrabSeatSelection = new(10, ["third", "first"]) });
        var vm = CreateViewModel(libraryService: libraries, settingsService: settings);
        vm.ManualCookieText = "cookie";
        await vm.ValidateManualCookieCommand.ExecuteAsync(null);
        vm.SelectedLibrary = second;
        // Preview alone must not replace the previously locked venue.
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(10, settings.CurrentSettings.LastLibraryId);
        Assert.Equal(10, settings.CurrentSettings.LastGrabSeatSelection!.LibraryId);
        await vm.BindSelectedLibraryCommand.ExecuteAsync(null);
        Assert.Equal(20, settings.CurrentSettings.LastGrabSeatSelection!.LibraryId);
        Assert.Empty(settings.CurrentSettings.LastGrabSeatSelection.SeatKeys);
        await vm.SignOutCommand.ExecuteAsync(null);
        vm.ManualCookieText = "cookie";
        await vm.ValidateManualCookieCommand.ExecuteAsync(null);
        Assert.Equal(20, libraries.BoundLibrary?.LibraryId);
        Assert.True(vm.IsCurrentLocked);
        Assert.Empty(vm.SelectedSeats);

        vm.VisibleSeats.Single(x => x.SeatKey == "second").IsSelected = true;
        vm.VisibleSeats.Single(x => x.SeatKey == "first").IsSelected = true;
        await vm.SeatSelectionSaveTask;
        // Multiple automatic restorations must neither freeze nor overwrite the saved order.
        await vm.ValidateManualCookieCommand.ExecuteAsync(null);
        await vm.ValidateManualCookieCommand.ExecuteAsync(null);
        Assert.Equal(["second", "first"], vm.SelectedSeats.Select(x => x.SeatKey));
        vm.MoveSelectedSeatUpCommand.Execute(vm.SelectedSeats[1]);
        await vm.SeatSelectionSaveTask;
        var fresh = CreateViewModel(libraryService: libraries, settingsService: settings);
        fresh.ManualCookieText = "cookie";
        await fresh.ValidateManualCookieCommand.ExecuteAsync(null);
        Assert.Equal(20, libraries.BoundLibrary?.LibraryId);
        Assert.True(fresh.IsCurrentLocked);
        Assert.Equal(["first", "second"], fresh.SelectedSeats.Select(x => x.SeatKey));
    }

    private static (LibrarySummary Library, FakeLibraryService Service) SelectionLibraries()
    {
        var library = new LibrarySummary(10, "场馆", "3", true, 3, 2, 0);
        var service = new FakeLibraryService { LibrariesToLoad = [library] };
        service.LayoutsByLibraryId[10] = new(10, "场馆", "3", true, 3, 0, 2,
            [new("first", "A1", false, 0, 0), new("second", "A2", false, 1, 0), new("third", "A3", true, 2, 0)]);
        return (library, service);
    }
}
