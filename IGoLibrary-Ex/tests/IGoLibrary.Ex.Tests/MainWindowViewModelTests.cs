using System.Net;
using System.Text;
using IGoLibrary.Ex.Desktop.Services;
using IGoLibrary.Ex.Desktop.ViewModels;
using IGoLibrary.Ex.Application.Services;
using IGoLibrary.Ex.Domain.Enums;
using IGoLibrary.Ex.Domain.Models;
using Avalonia.Media;

namespace IGoLibrary.Ex.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task ValidateManualCookieAsync_CachesWechatNickname_AndKeepsItOnSignOut()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default);
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetCurrentUserNicknameAsync = (_, _) => Task.FromResult<string?>("  星河  ")
        };
        var viewModel = CreateViewModel(apiClient: apiClient, settingsService: settingsService);
        viewModel.ManualCookieText = "Authorization=a; SERVERID=b";

        await viewModel.ValidateManualCookieCommand.ExecuteAsync(null);

        Assert.EndsWith("，星河", viewModel.HomeGreetingTitleText, StringComparison.Ordinal);
        Assert.Equal("星河", settingsService.CurrentSettings.CachedUserNickname);

        await viewModel.SignOutCommand.ExecuteAsync(null);

        Assert.EndsWith("，星河", viewModel.HomeGreetingTitleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_UsesCachedWechatNickname_WhenLoggedOut()
    {
        var viewModel = CreateViewModel(
            settingsService: new FakeSettingsService(AppSettings.Default with
            {
                CachedUserNickname = "之前的微信名"
            }));

        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsAuthorized);
        Assert.EndsWith("，之前的微信名", viewModel.HomeGreetingTitleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DailyCheckoutToggle_ConfiguresTaskImmediately_WhenEnabled()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default);
        var scheduler = new FakeDailyCheckoutTaskScheduler();
        var viewModel = CreateViewModel(
            settingsService: settingsService,
            dailyCheckoutTaskScheduler: scheduler);
        viewModel.DailyCheckoutEnabled = true;

        await viewModel.DailyCheckoutConfigurationTask;

        Assert.True(scheduler.LastEnabled);
        Assert.True(settingsService.CurrentSettings.DailyCheckoutEnabled);
        Assert.Contains("21:30", viewModel.DailyCheckoutStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DailyCheckoutToggle_UsesAndPersistsConfiguredTime()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default);
        var scheduler = new FakeDailyCheckoutTaskScheduler();
        var viewModel = CreateViewModel(
            settingsService: settingsService,
            dailyCheckoutTaskScheduler: scheduler);
        viewModel.DailyCheckoutTime = "08:15";
        viewModel.DailyCheckoutEnabled = true;

        await viewModel.DailyCheckoutConfigurationTask;

        Assert.Equal(new TimeSpan(8, 15, 0), scheduler.LastCheckoutTime);
        Assert.Equal("08:15", settingsService.CurrentSettings.DailyCheckoutTime);
        Assert.Contains("08:15", viewModel.DailyCheckoutStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DailyCheckoutToggle_RejectsInvalidConfiguredTime()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default);
        var scheduler = new FakeDailyCheckoutTaskScheduler();
        var viewModel = CreateViewModel(
            settingsService: settingsService,
            dailyCheckoutTaskScheduler: scheduler);
        viewModel.DailyCheckoutTime = "25:99";
        viewModel.DailyCheckoutEnabled = true;

        await viewModel.DailyCheckoutConfigurationTask;

        Assert.False(viewModel.DailyCheckoutEnabled);
        Assert.False(settingsService.CurrentSettings.DailyCheckoutEnabled);
        Assert.Null(scheduler.LastCheckoutTime);
        Assert.Contains("退座时间格式不正确", viewModel.DailyCheckoutStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DailyCheckoutToggle_DoesNotPersistSetting_WhenTaskCreationFails()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default);
        var scheduler = new FakeDailyCheckoutTaskScheduler
        {
            Exception = new InvalidOperationException("task scheduler unavailable")
        };
        var viewModel = CreateViewModel(
            settingsService: settingsService,
            dailyCheckoutTaskScheduler: scheduler);
        viewModel.DailyCheckoutEnabled = true;

        await viewModel.DailyCheckoutConfigurationTask;

        Assert.False(viewModel.DailyCheckoutEnabled);
        Assert.False(settingsService.CurrentSettings.DailyCheckoutEnabled);
        Assert.Contains("任务配置失败", viewModel.DailyCheckoutStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateManualCookieAsync_DoesNotRestoreStoredVenueSelection_OnFreshAuthorization()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default with
        {
            LastLibraryId = 1,
            LastLibraryName = "场馆A"
        });
        var libraryService = new FakeLibraryService
        {
            LibrariesToLoad =
            [
                new LibrarySummary(1, "场馆A", "3层", true, 120, 20, 10),
                new LibrarySummary(2, "场馆B", "5层", true, 80, 10, 5)
            ]
        };
        var viewModel = CreateViewModel(
            sessionService: new FakeSessionService(),
            libraryService: libraryService,
            settingsService: settingsService);

        viewModel.ManualCookieText = "Authorization=a; SERVERID=b";

        await viewModel.ValidateManualCookieCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsAuthorized);
        Assert.Null(viewModel.SelectedLibrary);
        Assert.Equal(1, libraryService.LoadLibrariesCalls);
    }

    [Fact]
    public void SidebarItems_ExposeGuideLast_WhenUnauthorized()
    {
        var viewModel = CreateViewModel();

        var titles = viewModel.SidebarItems.Select(item => item.Title).ToArray();

        Assert.Equal(["首页", "账户与场馆", "使用指南"], titles);
    }

    [Fact]
    public void SidebarItems_ExposeRestrictedEntries_WhenAuthorized()
    {
        var viewModel = CreateViewModel();

        viewModel.IsAuthorized = true;

        var titles = viewModel.SidebarItems.Select(item => item.Title).ToArray();

        Assert.Equal(["首页", "账户与场馆", "抢座", "占座", "退座", "通知设置", "系统设置", "使用指南"], titles);
    }

    [Fact]
    public void Guide_RemainsAccessible_WhenUnauthorized()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedTabIndex = MainWindowViewModel.GuideTabIndex;

        Assert.Equal(MainWindowViewModel.GuideTabIndex, viewModel.SelectedTabIndex);
        Assert.Equal("使用指南", viewModel.SelectedSidebarItem?.Title);
    }

    [Fact]
    public void SigningOut_WhileGuideIsOpen_KeepsGuideSelected()
    {
        var viewModel = CreateViewModel();
        viewModel.IsAuthorized = true;
        viewModel.SelectedTabIndex = MainWindowViewModel.GuideTabIndex;

        viewModel.IsAuthorized = false;

        Assert.Equal(MainWindowViewModel.GuideTabIndex, viewModel.SelectedTabIndex);
        Assert.Equal("使用指南", viewModel.SelectedSidebarItem?.Title);
    }

    [Fact]
    public async Task NotificationSettings_AutoSaveCookieExpiryAlerts_WhenFieldsChange()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default);
        var viewModel = CreateViewModel(settingsService: settingsService);
        await viewModel.InitializeAsync();

        viewModel.CookieAlertSmtpHost = "smtp.example.com";
        viewModel.CookieAlertSmtpPort = 465;
        viewModel.CookieEmailAlertsEnabled = true;

        await WaitForAsync(() => settingsService.SaveCalls > 0 && settingsService.CurrentSettings.CookieExpiryAlerts?.Email.SmtpHost == "smtp.example.com");

        var alerts = Assert.IsType<CookieExpiryAlertSettings>(settingsService.CurrentSettings.CookieExpiryAlerts);
        Assert.True(alerts.Email.Enabled);
        Assert.Equal("smtp.example.com", alerts.Email.SmtpHost);
        Assert.Equal(465, alerts.Email.Port);
    }

    [Fact]
    public async Task SendTestEmailAlertAsync_UsesCurrentNotificationSettingsSnapshot()
    {
        var alertService = new FakeTaskAlertService();
        var viewModel = CreateViewModel(taskAlertService: alertService);
        await viewModel.InitializeAsync();

        viewModel.CookieAlertSmtpHost = "smtp.example.com";
        viewModel.CookieAlertSmtpPort = 587;
        viewModel.SelectedCookieAlertSecurityModeIndex = 1;
        viewModel.CookieAlertUsername = "tester";
        viewModel.CookieAlertPassword = "secret";
        viewModel.CookieAlertFromAddress = "from@example.com";
        viewModel.CookieAlertToAddress = "to@example.com";

        await viewModel.SendTestEmailAlertCommand.ExecuteAsync(null);

        var request = Assert.Single(alertService.TestEmailRequests);
        Assert.Equal("smtp.example.com", request.SmtpHost);
        Assert.Equal(587, request.Port);
        Assert.Equal(EmailSecurityMode.Tls, request.SecurityMode);
        Assert.Equal("tester", request.Username);
        Assert.Equal("secret", request.Password);
        Assert.Equal("from@example.com", request.FromAddress);
        Assert.Equal("to@example.com", request.ToAddress);
    }

    [Fact]
    public async Task SendTestEmailAlertAsync_ShowsErrorDialog_WhenSendingFails()
    {
        var alertService = new FakeTaskAlertService
        {
            SendTestEmailException = new InvalidOperationException("smtp connect failed")
        };
        var errorDialogService = new FakeErrorDialogService();
        var viewModel = CreateViewModel(
            taskAlertService: alertService,
            errorDialogService: errorDialogService);
        await viewModel.InitializeAsync();

        viewModel.CookieAlertSmtpHost = "smtp.example.com";
        viewModel.CookieAlertFromAddress = "from@example.com";
        viewModel.CookieAlertToAddress = "to@example.com";

        await viewModel.SendTestEmailAlertCommand.ExecuteAsync(null);

        var error = Assert.Single(errorDialogService.Errors);
        Assert.Equal("测试邮件发送失败", error.Title);
        Assert.Equal(nameof(InvalidOperationException), error.ErrorType);
        Assert.Equal("smtp connect failed", error.ErrorMessage);
    }

    [Fact]
    public async Task TryAutoParseClipboardLinkAsync_DoesNotConsumeSameCodeTwice()
    {
        var notificationService = new FakeNotificationService();
        var apiClient = new FakeTraceIntApiClient();
        var getCookieCalls = 0;
        apiClient.OnGetCookieFromCodeAsync = (code, _) =>
        {
            getCookieCalls++;
            return Task.FromResult("Authorization=a; SERVERID=b");
        };

        var viewModel = CreateViewModel(
            apiClient: apiClient,
            notificationService: notificationService);

        const string link = "https://example.com/callback?code=1234567890abcdef1234567890abcdef&state=1";

        var firstResult = await viewModel.TryAutoParseClipboardLinkAsync(link);
        var secondResult = await viewModel.TryAutoParseClipboardLinkAsync(link);

        Assert.True(firstResult);
        Assert.False(secondResult);
        Assert.Equal(1, getCookieCalls);
        Assert.Contains(notificationService.Successes, item => item.Title == "已成功获取 Cookie");
    }

    [Fact]
    public async Task TryAutoParseClipboardLinkAsync_ShowsCookieExpirationTime_WhenJwtCookieHasExpireAt()
    {
        var notificationService = new FakeNotificationService();
        var expiresAt = new DateTimeOffset(2026, 5, 5, 16, 56, 0, DateTimeOffset.Now.Offset);
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetCookieFromCodeAsync = (_, _) => Task.FromResult(BuildAuthorizationCookie(expiresAt))
        };
        var viewModel = CreateViewModel(
            apiClient: apiClient,
            notificationService: notificationService);

        const string link = "https://example.com/callback?code=1234567890abcdef1234567890abcdef&state=1";

        var result = await viewModel.TryAutoParseClipboardLinkAsync(link);

        Assert.True(result);
        var success = Assert.Single(notificationService.Successes, item => item.Title == "已成功获取 Cookie");
        Assert.Equal(
            $"授权链接解析成功，Cookie 已填入。{Environment.NewLine}Cookie 到期时间：5月5日 16:56",
            success.Message);
    }

    [Fact]
    public async Task TryAutoParseClipboardLinkAsync_AllowsRetry_WhenFirstCookieFetchFailsBeforeCookieIsIssued()
    {
        var notificationService = new FakeNotificationService();
        var apiClient = new FakeTraceIntApiClient();
        var getCookieCalls = 0;
        apiClient.OnGetCookieFromCodeAsync = (_, _) =>
        {
            getCookieCalls++;
            if (getCookieCalls == 1)
            {
                throw new HttpRequestException("temporary network failure");
            }

            return Task.FromResult("Authorization=a; SERVERID=b");
        };

        var viewModel = CreateViewModel(
            apiClient: apiClient,
            notificationService: notificationService);

        const string link = "https://example.com/callback?code=1234567890abcdef1234567890abcdef&state=1";

        var firstResult = await viewModel.TryAutoParseClipboardLinkAsync(link);
        var secondResult = await viewModel.TryAutoParseClipboardLinkAsync(link);

        Assert.False(firstResult);
        Assert.True(secondResult);
        Assert.Equal(2, getCookieCalls);
        Assert.Contains(notificationService.Warnings, item => item.Title == "获取 Cookie 失败");
        Assert.Contains(notificationService.Successes, item => item.Title == "已成功获取 Cookie");
    }

    [Fact]
    public async Task InitializeAsync_ShowsSuccessToast_WhenStoredJwtCookieIsRestored()
    {
        var notificationService = new FakeNotificationService();
        var expiresAt = DateTimeOffset.Now.AddHours(2);
        var sessionService = new FakeSessionService
        {
            RestoreResult = new SessionCredentials(
                BuildAuthorizationCookie(expiresAt),
                SessionSource.ManualCookie,
                DateTimeOffset.Now,
                true)
        };
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            notificationService: notificationService);

        await viewModel.InitializeAsync();

        var success = Assert.Single(notificationService.Successes, item => item.Title == "已成功恢复上次的 Cookie");
        Assert.Equal($"Cookie 到期时间：{expiresAt:M月d日 HH:mm}", success.Message);
    }

    [Fact]
    public async Task InitializeAsync_ShowsWarningToast_WhenRestoredJwtCookieExpiresSoon()
    {
        var notificationService = new FakeNotificationService();
        var expiresAt = DateTimeOffset.Now.AddMinutes(20);
        var sessionService = new FakeSessionService
        {
            RestoreResult = new SessionCredentials(
                BuildAuthorizationCookie(expiresAt),
                SessionSource.ManualCookie,
                DateTimeOffset.Now,
                true)
        };
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            notificationService: notificationService);

        await viewModel.InitializeAsync();

        var warning = Assert.Single(notificationService.Warnings, item => item.Title == "已成功恢复上次的 Cookie，注意到期时间");
        Assert.Equal($"Cookie 到期时间：{expiresAt:M月d日 HH:mm}", warning.Message);
    }

    [Fact]
    public async Task ValidateManualCookieAsync_ShowsSidebarCookieExpiration_WhenJwtCookieHasExpireAt()
    {
        var expiresAt = new DateTimeOffset(2026, 5, 5, 16, 56, 0, DateTimeOffset.Now.Offset);
        var viewModel = CreateViewModel();
        viewModel.ManualCookieText = BuildAuthorizationCookie(expiresAt);

        await viewModel.ValidateManualCookieCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasSidebarCookieExpiry);
        Assert.Equal("5月5日 16:56", viewModel.SidebarCookieExpiryText);
    }

    [Fact]
    public async Task ValidateManualCookieAsync_UsesWarningBrush_WhenCookieExpiresWithinThirtyMinutes()
    {
        var viewModel = CreateViewModel();
        viewModel.ManualCookieText = BuildAuthorizationCookie(DateTimeOffset.Now.AddMinutes(20));

        await viewModel.ValidateManualCookieCommand.ExecuteAsync(null);

        Assert.Equal("#FFC27803", GetBrushColor(viewModel.SidebarCookieExpiryBrush).ToString(), ignoreCase: true);
    }

    [Fact]
    public async Task ValidateManualCookieAsync_UsesFailureBrush_WhenCookieExpiresWithinTenMinutes()
    {
        var viewModel = CreateViewModel();
        viewModel.ManualCookieText = BuildAuthorizationCookie(DateTimeOffset.Now.AddMinutes(5));

        await viewModel.ValidateManualCookieCommand.ExecuteAsync(null);

        Assert.Equal("#FFC93C37", GetBrushColor(viewModel.SidebarCookieExpiryBrush).ToString(), ignoreCase: true);
    }

    [Fact]
    public async Task SignOutAsync_HidesSidebarCookieExpiration()
    {
        var expiresAt = DateTimeOffset.Now.AddHours(2);
        var viewModel = CreateViewModel();
        viewModel.ManualCookieText = BuildAuthorizationCookie(expiresAt);
        await viewModel.ValidateManualCookieCommand.ExecuteAsync(null);

        await viewModel.SignOutCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasSidebarCookieExpiry);
        Assert.Equal(string.Empty, viewModel.SidebarCookieExpiryText);
    }

    [Fact]
    public async Task SignOutAsync_ClearsStoredLastLibrarySelection()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default with
        {
            LastLibraryId = 1,
            LastLibraryName = "场馆A"
        });
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            settingsService: settingsService);

        viewModel.IsAuthorized = true;
        viewModel.SelectedTabIndex = 4;
        viewModel.SelectedLibrary = new LibrarySummary(1, "场馆A", "3层", true, 120, 20, 10);

        await viewModel.SignOutCommand.ExecuteAsync(null);

        Assert.Equal(1, sessionService.SignOutCalls);
        Assert.False(viewModel.IsAuthorized);
        Assert.Equal(MainWindowViewModel.AccountAndVenueTabIndex, viewModel.SelectedTabIndex);
        Assert.Null(viewModel.SelectedLibrary);
        Assert.Null(settingsService.CurrentSettings.LastLibraryId);
        Assert.Null(settingsService.CurrentSettings.LastLibraryName);
    }

    [Fact]
    public async Task OpenVenuePickerAsync_PreservesCurrentLockedLibrary_WhenOneIsAlreadyBound()
    {
        var libraryA = new LibrarySummary(1, "场馆A", "3层", true, 120, 20, 10);
        var libraryB = new LibrarySummary(2, "场馆B", "5层", true, 80, 10, 5);
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var settingsService = new FakeSettingsService(AppSettings.Default with
        {
            LastLibraryId = libraryB.LibraryId,
            LastLibraryName = libraryB.Name
        });
        var libraryService = new FakeLibraryService
        {
            LibrariesToLoad = [libraryA, libraryB]
        };
        libraryService.LayoutsByLibraryId[libraryA.LibraryId] = new LibraryLayout(
            libraryA.LibraryId,
            libraryA.Name,
            libraryA.Floor,
            libraryA.IsOpen,
            120,
            10,
            20,
            [new SeatSnapshot("seat-1", "1", false, 0, 0)]);

        var apiClient = new FakeTraceIntApiClient
        {
            OnGetLibraryRuleAsync = (_, _, _) => Task.FromResult(new LibraryRule(
                libraryA.LibraryId,
                "1小时",
                "30",
                "30",
                "0",
                "{}",
                null,
                null,
                0,
                "07:30",
                0,
                "22:00",
                -1))
        };
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            libraryService: libraryService,
            settingsService: settingsService,
            apiClient: apiClient);

        viewModel.IsAuthorized = true;
        viewModel.SelectedLibrary = libraryA;

        await viewModel.BindSelectedLibraryCommand.ExecuteAsync(null);
        await viewModel.OpenVenuePickerCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsVenuePickerOpen);
        Assert.Equal(libraryA.LibraryId, viewModel.SelectedLibrary?.LibraryId);
        Assert.Equal(1, libraryService.LoadLibrariesCalls);
    }

    [Fact]
    public async Task GrabDashboardStatusBrush_UsesFailureColor_WhenTaskCompletedByStopping()
    {
        var grabCoordinator = new FakeGrabSeatCoordinator();
        await grabCoordinator.StopAsync();

        var viewModel = CreateViewModel(grabSeatCoordinator: grabCoordinator);
        await viewModel.InitializeAsync();

        var brush = Assert.IsType<SolidColorBrush>(viewModel.GrabDashboardStatusBrush);

        Assert.Equal("已停止", viewModel.GrabDashboardStatusText);
        Assert.Equal(Color.Parse("#C93C37"), brush.Color);
    }

    [Fact]
    public async Task StartGrabAsync_StartsTomorrowReservationCoordinator_WhenTargetIsTomorrow()
    {
        var library = new LibrarySummary(10, "自科阅览区", "3", true, 120, 10, 0);
        var libraryService = new FakeLibraryService
        {
            LibrariesToLoad = [library]
        };
        libraryService.LayoutsByLibraryId[library.LibraryId] = new LibraryLayout(
            library.LibraryId,
            library.Name,
            library.Floor,
            library.IsOpen,
            120,
            0,
            10,
            [new SeatSnapshot("10,79", "225", false, 0, 0)]);
        var tomorrowCoordinator = new FakeTomorrowReservationCoordinator();
        var viewModel = CreateViewModel(
            libraryService: libraryService,
            apiClient: new FakeTraceIntApiClient
            {
                OnGetLibraryRuleAsync = (_, _, _) => Task.FromResult(new LibraryRule(
                    library.LibraryId,
                    "1小时",
                    "30",
                    "30",
                    "0",
                    "{}",
                    null,
                    null,
                    0,
                    "07:30",
                    0,
                    "22:00",
                    -1))
            },
            tomorrowReservationCoordinator: tomorrowCoordinator);

        viewModel.IsAuthorized = true;
        viewModel.SelectedLibrary = library;
        await viewModel.BindSelectedLibraryCommand.ExecuteAsync(null);
        viewModel.VisibleSeats[0].IsSelected = true;
        viewModel.SelectedGrabTaskTargetIndex = 1;
        viewModel.ScheduledTimeText = "19:59:55";

        await viewModel.StartGrabCommand.ExecuteAsync(null);

        Assert.Equal(1, tomorrowCoordinator.StartCalls);
        Assert.NotNull(tomorrowCoordinator.StartedPlan);
        var plan = tomorrowCoordinator.StartedPlan;
        Assert.Equal(library.LibraryId, plan.LibraryId);
        Assert.Equal("225", Assert.Single(plan.Seats).SeatName);
        Assert.Equal(new TimeOnly(19, 59, 55), plan.ScheduledStart);
    }

    [Fact]
    public async Task StartRandomAvailableSeatGrabAsync_StartsGrabCoordinatorWithoutSelectedSeats()
    {
        var library = new LibrarySummary(10, "library", "3", true, 120, 10, 0);
        var grabCoordinator = new FakeGrabSeatCoordinator();
        var viewModel = CreateViewModel(grabSeatCoordinator: grabCoordinator);

        viewModel.IsAuthorized = true;
        viewModel.SelectedLibrary = library;
        viewModel.SelectedGrabTaskTargetIndex = 0;
        viewModel.ScheduledTimeText = "08:15:30";

        await viewModel.StartRandomAvailableSeatGrabCommand.ExecuteAsync(null);

        Assert.Equal(1, grabCoordinator.StartCalls);
        var plan = Assert.IsType<GrabSeatPlan>(grabCoordinator.StartedPlan);
        Assert.True(plan.UseRandomAvailableSeat);
        Assert.Empty(plan.Seats);
        Assert.Equal(library.LibraryId, plan.LibraryId);
        Assert.Equal(new TimeOnly(8, 15, 30), plan.ScheduledStart);
    }

    [Fact]
    public async Task StartRandomAvailableSeatGrabAsync_StartsTomorrowCoordinatorWithoutSelectedSeats()
    {
        var library = new LibrarySummary(10, "library", "3", true, 120, 10, 0);
        var grabCoordinator = new FakeGrabSeatCoordinator();
        var tomorrowCoordinator = new FakeTomorrowReservationCoordinator();
        var viewModel = CreateViewModel(
            grabSeatCoordinator: grabCoordinator,
            tomorrowReservationCoordinator: tomorrowCoordinator);

        viewModel.IsAuthorized = true;
        viewModel.SelectedLibrary = library;
        viewModel.SelectedGrabTaskTargetIndex = 1;
        viewModel.ScheduledTimeText = "08:15:30";

        Assert.True(viewModel.CanStartRandomAvailableSeatGrab);
        await viewModel.StartRandomAvailableSeatGrabCommand.ExecuteAsync(null);

        Assert.Equal(0, grabCoordinator.StartCalls);
        Assert.Equal(1, tomorrowCoordinator.StartCalls);
        var plan = Assert.IsType<TomorrowReservationPlan>(tomorrowCoordinator.StartedPlan);
        Assert.True(plan.UseRandomAvailableSeat);
        Assert.Empty(plan.Seats);
        Assert.Equal(library.LibraryId, plan.LibraryId);
        Assert.Equal(new TimeOnly(8, 15, 30), plan.ScheduledStart);
    }

    [Fact]
    public async Task StartOccupyAsync_StartsCoordinatorWithScheduledReReserveTime()
    {
        var occupyCoordinator = new FakeOccupySeatCoordinator();
        var viewModel = CreateViewModel(occupySeatCoordinator: occupyCoordinator);

        viewModel.SelectedOccupyReReserveTriggerModeIndex = 1;
        viewModel.ReReserveLeadMinutes = 2;
        viewModel.ReReserveDelaySeconds = 30;
        viewModel.OccupyScheduledReReserveTimeText = "14:25:30";

        await viewModel.StartOccupyCommand.ExecuteAsync(null);

        Assert.Equal(1, occupyCoordinator.StartCalls);
        var plan = Assert.IsType<OccupySeatPlan>(occupyCoordinator.StartedPlan);
        Assert.Equal(OccupyReReserveTriggerMode.ScheduledTime, plan.TriggerMode);
        Assert.Equal(TimeSpan.FromSeconds(150), plan.ReReserveLeadTime);
        Assert.Equal(new TimeOnly(14, 25, 30), plan.ScheduledReReserveTime);
    }

    [Fact]
    public async Task StopOccupyAsync_FreezesReReserveCountdown_WhenOccupyTaskStops()
    {
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetReservationRecordsAsync = (_, _) => Task.FromResult<IReadOnlyList<ReservationRecord>>(
            [
                new ReservationRecord(
                    ReservationRecordKind.Today,
                    "token-1",
                    1,
                    "电子阅览室",
                    "seat-a701",
                    "A701",
                    DateTimeOffset.Now.AddMinutes(30),
                    DateOnly.FromDateTime(DateTime.Now))
            ])
        };
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            apiClient: apiClient);

        viewModel.SelectedOccupyReReserveTriggerModeIndex = 0;
        viewModel.ReReserveLeadMinutes = 1;
        viewModel.ReReserveDelaySeconds = 0;

        await viewModel.RefreshReservationCommand.ExecuteAsync(null);
        Assert.Equal("占座已停止", viewModel.ReservationCountdownText);

        viewModel.IsOccupyRunning = true;
        Assert.StartsWith("倒计时：", viewModel.ReservationCountdownText);

        viewModel.IsOccupyRunning = false;
        Assert.False(viewModel.IsOccupyRunning);
        Assert.Equal("占座已停止", viewModel.ReservationCountdownText);
    }

    [Fact]
    public async Task InitializeAsync_LoadsDashboardMetricsIntoHomeCards()
    {
        var viewModel = CreateViewModel(settingsService: new FakeSettingsService(AppSettings.Default with
        {
            SuccessfulReservationCount = 7,
            TotalGuardSeconds = 5400
        }));

        await viewModel.InitializeAsync();

        Assert.Equal(7, viewModel.HomeHistoricalSuccessCount);
        Assert.Equal("1 小时 30 分", viewModel.HomeTotalGuardDurationText);
    }

    [Fact]
    public async Task SaveSettingsAsync_PreservesDashboardMetrics()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default with
        {
            SuccessfulReservationCount = 4,
            TotalGuardSeconds = 7200
        });
        var viewModel = CreateViewModel(settingsService: settingsService);
        await viewModel.InitializeAsync();

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(4, settingsService.CurrentSettings.SuccessfulReservationCount);
        Assert.Equal(7200, settingsService.CurrentSettings.TotalGuardSeconds);
    }

    [Fact]
    public async Task SaveSettingsAsync_PersistsThemePreferences()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default);
        var themeService = new FakeAppThemeService();
        var viewModel = CreateViewModel(
            settingsService: settingsService,
            appThemeService: themeService);
        await viewModel.InitializeAsync();

        viewModel.SelectedAppThemeModeIndex = 2;
        viewModel.UseSystemAccent = false;

        await WaitForAsync(() => themeService.ApplySettingsCalls == 2);

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(AppThemeMode.Dark, settingsService.CurrentSettings.ThemeMode);
        Assert.False(settingsService.CurrentSettings.UseSystemAccent);
        Assert.Equal(3, themeService.ApplySettingsCalls);
        Assert.Equal(AppThemeMode.Dark, themeService.LastAppliedSettings?.ThemeMode);
        Assert.False(themeService.LastAppliedSettings?.UseSystemAccent);
    }

    [Fact]
    public async Task ThemePreview_UpdatesImmediately_WithoutSavingSettings()
    {
        var settingsService = new FakeSettingsService(AppSettings.Default);
        var themeService = new FakeAppThemeService();
        var viewModel = CreateViewModel(
            settingsService: settingsService,
            appThemeService: themeService);
        await viewModel.InitializeAsync();

        viewModel.SelectedAppThemeModeIndex = 2;
        viewModel.UseSystemAccent = false;

        await WaitForAsync(() =>
            themeService.ApplySettingsCalls == 2 &&
            themeService.LastAppliedSettings?.ThemeMode == AppThemeMode.Dark &&
            themeService.LastAppliedSettings?.UseSystemAccent == false);

        Assert.Equal(0, settingsService.SaveCalls);
        Assert.Equal(AppThemeMode.FollowSystem, settingsService.CurrentSettings.ThemeMode);
        Assert.True(settingsService.CurrentSettings.UseSystemAccent);
    }

    [Fact]
    public async Task CancelCurrentReservationAsync_ClearsHomeReservationCard_WhenApiSucceeds()
    {
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetReservationInfoAsync = (_, _) => Task.FromResult<ReservationInfo?>(new ReservationInfo(
                "token-1",
                1,
                "自科阅览区一",
                "seat-4",
                "4",
                DateTimeOffset.Now.AddMinutes(30))),
            OnCancelReservationAsync = (_, _, _) => Task.FromResult(true)
        };
        var notifications = new FakeNotificationService();
        var confirmationDialogService = new FakeConfirmationDialogService();
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            apiClient: apiClient,
            notificationService: notifications,
            confirmationDialogService: confirmationDialogService);

        await viewModel.RefreshReservationCommand.ExecuteAsync(null);
        await viewModel.CancelCurrentReservationCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasNoCurrentReservation);
        Assert.Equal("--", viewModel.HomeReservationSeatNumberText);
        Assert.Contains(notifications.Successes, x => x.Title == "已取消预约");
        Assert.Single(confirmationDialogService.Requests);
    }

    [Fact]
    public async Task CancelCurrentReservationAsync_KeepsTomorrowRecord_WhenTodayCancelled()
    {
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetReservationRecordsAsync = (_, _) => Task.FromResult<IReadOnlyList<ReservationRecord>>(
            [
                new ReservationRecord(
                    ReservationRecordKind.Today,
                    "today-token",
                    1,
                    "电子阅览室",
                    "seat-4",
                    "4",
                    DateTimeOffset.Now.AddMinutes(30),
                    DateOnly.FromDateTime(DateTime.Now)),
                new ReservationRecord(
                    ReservationRecordKind.Tomorrow,
                    "tomorrow-token",
                    2,
                    "社科阅览室",
                    "seat-8",
                    "8",
                    null,
                    DateOnly.FromDateTime(DateTime.Now.AddDays(1)))
            ]),
            OnCancelReservationAsync = (_, _, _) => Task.FromResult(true)
        };
        var confirmationDialogService = new FakeConfirmationDialogService();
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            apiClient: apiClient,
            confirmationDialogService: confirmationDialogService);

        await viewModel.RefreshReservationCommand.ExecuteAsync(null);
        await viewModel.CancelCurrentReservationCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasNoCurrentReservation);
        Assert.Single(viewModel.HomeReservationRecords);
        Assert.Equal("明日预约", viewModel.HomeReservationRecords[0].KindText);
        Assert.Equal("社科阅览室", viewModel.HomeReservationRecords[0].VenueText);
        Assert.Single(confirmationDialogService.Requests);
    }

    [Fact]
    public async Task CancelReservationRecordAsync_CancelsTodayRecord_WithReservationToken()
    {
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        string? cancelledToken = null;
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetReservationRecordsAsync = (_, _) => Task.FromResult<IReadOnlyList<ReservationRecord>>(
            [
                new ReservationRecord(
                    ReservationRecordKind.Today,
                    "today-token",
                    1,
                    "电子阅览室",
                    "seat-4",
                    "4",
                    DateTimeOffset.Now.AddMinutes(30),
                    DateOnly.FromDateTime(DateTime.Now)),
                new ReservationRecord(
                    ReservationRecordKind.Tomorrow,
                    "tomorrow-token",
                    2,
                    "社科阅览室",
                    "seat-8",
                    "8",
                    null,
                    DateOnly.FromDateTime(DateTime.Now.AddDays(1)))
            ]),
            OnCancelReservationAsync = (_, token, _) =>
            {
                cancelledToken = token;
                return Task.FromResult(true);
            }
        };
        var confirmationDialogService = new FakeConfirmationDialogService();
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            apiClient: apiClient,
            confirmationDialogService: confirmationDialogService);

        await viewModel.RefreshReservationCommand.ExecuteAsync(null);
        var today = viewModel.HomeReservationRecords.First(record => record.KindText == "今日预约");
        await viewModel.CancelReservationRecordCommand.ExecuteAsync(today);

        Assert.Equal("today-token", cancelledToken);
        Assert.Single(viewModel.HomeReservationRecords);
        Assert.Equal("明日预约", viewModel.HomeReservationRecords[0].KindText);
        Assert.Single(confirmationDialogService.Requests);
    }

    [Fact]
    public async Task CancelReservationRecordAsync_CancelsTomorrowRecord_WithPrereserveCancelMutation()
    {
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var cancelPrereserveCalls = 0;
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetReservationRecordsAsync = (_, _) => Task.FromResult<IReadOnlyList<ReservationRecord>>(
            [
                new ReservationRecord(
                    ReservationRecordKind.Today,
                    "today-token",
                    1,
                    "电子阅览室",
                    "seat-4",
                    "4",
                    DateTimeOffset.Now.AddMinutes(30),
                    DateOnly.FromDateTime(DateTime.Now)),
                new ReservationRecord(
                    ReservationRecordKind.Tomorrow,
                    "tomorrow-token",
                    2,
                    "社科阅览室",
                    "seat-8",
                    "8",
                    null,
                    DateOnly.FromDateTime(DateTime.Now.AddDays(1)))
            ]),
            OnCancelPrereserveAsync = (_, _) =>
            {
                cancelPrereserveCalls++;
                return Task.FromResult(true);
            }
        };
        var confirmationDialogService = new FakeConfirmationDialogService();
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            apiClient: apiClient,
            confirmationDialogService: confirmationDialogService);

        await viewModel.RefreshReservationCommand.ExecuteAsync(null);
        var tomorrow = viewModel.HomeReservationRecords.First(record => record.KindText == "明日预约");
        await viewModel.CancelReservationRecordCommand.ExecuteAsync(tomorrow);

        Assert.Equal(1, cancelPrereserveCalls);
        Assert.Single(viewModel.HomeReservationRecords);
        Assert.Equal("今日预约", viewModel.HomeReservationRecords[0].KindText);
        Assert.True(viewModel.HasCurrentReservation);
        Assert.Single(confirmationDialogService.Requests);
    }

    [Fact]
    public async Task CancelReservationRecordAsync_DoesNotCallApi_WhenConfirmationIsCancelled()
    {
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var cancelCalls = 0;
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetReservationRecordsAsync = (_, _) => Task.FromResult<IReadOnlyList<ReservationRecord>>(
            [
                new ReservationRecord(
                    ReservationRecordKind.Today,
                    "today-token",
                    1,
                    "电子阅览室",
                    "seat-4",
                    "4",
                    DateTimeOffset.Now.AddMinutes(30),
                    DateOnly.FromDateTime(DateTime.Now))
            ]),
            OnCancelReservationAsync = (_, _, _) =>
            {
                cancelCalls++;
                return Task.FromResult(true);
            }
        };
        var confirmationDialogService = new FakeConfirmationDialogService
        {
            NextResult = false
        };
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            apiClient: apiClient,
            confirmationDialogService: confirmationDialogService);

        await viewModel.RefreshReservationCommand.ExecuteAsync(null);
        var today = Assert.Single(viewModel.HomeReservationRecords);
        await viewModel.CancelReservationRecordCommand.ExecuteAsync(today);

        Assert.Equal(0, cancelCalls);
        Assert.Single(viewModel.HomeReservationRecords);
        Assert.Single(confirmationDialogService.Requests);
    }

    [Fact]
    public async Task RefreshReservationAsync_ShowsCheckedInTodayReservationAsStudyingWithoutCancel()
    {
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var cancelCalls = 0;
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetReservationRecordsAsync = (_, _) => Task.FromResult<IReadOnlyList<ReservationRecord>>(
            [
                new ReservationRecord(
                    ReservationRecordKind.Today,
                    "today-token",
                    1,
                    "电子阅览室",
                    "seat-4",
                    "4",
                    DateTimeOffset.Now.AddMinutes(30),
                    DateOnly.FromDateTime(DateTime.Now),
                    IsCheckedIn: true)
            ]),
            OnCancelReservationAsync = (_, _, _) =>
            {
                cancelCalls++;
                return Task.FromResult(true);
            }
        };
        var notifications = new FakeNotificationService();
        var confirmationDialogService = new FakeConfirmationDialogService();
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            apiClient: apiClient,
            notificationService: notifications,
            confirmationDialogService: confirmationDialogService);

        await viewModel.RefreshReservationCommand.ExecuteAsync(null);

        var record = Assert.Single(viewModel.HomeReservationRecords);
        Assert.Equal("状态", record.RemainingLabelText);
        Assert.Equal("学习中", record.RemainingText);
        Assert.Equal("学习中", record.BadgeText);
        Assert.True(record.CanCancel);
        Assert.True(viewModel.CanCancelCurrentReservation);

        await viewModel.CancelReservationRecordCommand.ExecuteAsync(record);

        Assert.Equal(1, cancelCalls);
        var request = Assert.Single(confirmationDialogService.Requests);
        Assert.Equal("确认退座", request.Title);
        Assert.Equal("退座", request.ConfirmText);
        Assert.Contains(notifications.Successes, success => success.Title == "已退座");
    }

    [Fact]
    public async Task RefreshReservationAsync_ShowsTodayAndTomorrowReservationRecords()
    {
        var sessionService = new FakeSessionService
        {
            CurrentSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now, true)
        };
        var apiClient = new FakeTraceIntApiClient
        {
            OnGetReservationRecordsAsync = (_, _) => Task.FromResult<IReadOnlyList<ReservationRecord>>(
            [
                new ReservationRecord(
                    ReservationRecordKind.Today,
                    "today-token",
                    1,
                    "电子阅览室",
                    "seat-4",
                    "4",
                    DateTimeOffset.Now.AddMinutes(30),
                    DateOnly.FromDateTime(DateTime.Now)),
                new ReservationRecord(
                    ReservationRecordKind.Tomorrow,
                    "tomorrow-token",
                    2,
                    "社科阅览室",
                    "seat-8",
                    "8",
                    null,
                    DateOnly.FromDateTime(DateTime.Now.AddDays(1)))
            ])
        };
        var viewModel = CreateViewModel(
            sessionService: sessionService,
            apiClient: apiClient);

        await viewModel.RefreshReservationCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasReservationRecords);
        Assert.Equal(2, viewModel.HomeReservationRecords.Count);
        Assert.Contains(viewModel.HomeReservationRecords, record => record.KindText == "今日预约" && record.VenueText == "电子阅览室");
        Assert.Contains(viewModel.HomeReservationRecords, record => record.KindText == "明日预约" && record.VenueText == "社科阅览室");
        Assert.True(viewModel.HasCurrentReservation);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_InstallsUpdate_WhenConfirmed()
    {
        var appUpdateService = new FakeAppUpdateService
        {
            Result = new AppUpdateCheckResult(
                true,
                "1.0.0",
                "1.0.1",
                "修复更新流程。",
                "https://github.com/Luofaiz/IGoLibrary/releases/latest/download/IGoLibrarySetup.exe",
                "abc123",
                "https://github.com/Luofaiz/IGoLibrary/releases/latest")
        };
        var notifications = new FakeNotificationService();
        var confirmationDialogService = new FakeConfirmationDialogService();
        var viewModel = CreateViewModel(
            notificationService: notifications,
            confirmationDialogService: confirmationDialogService,
            appUpdateService: appUpdateService);

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, appUpdateService.CheckCalls);
        Assert.Equal(1, appUpdateService.InstallCalls);
        Assert.Equal(appUpdateService.Result, appUpdateService.LastInstallUpdate);
        var request = Assert.Single(confirmationDialogService.Requests);
        Assert.Equal("安装更新", request.ConfirmText);
        Assert.Contains(notifications.Infos, item => item.Title == "安装程序已启动");
        Assert.Equal("安装程序已启动，请按向导完成更新。", viewModel.UpdateStatusText);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_DoesNotInstallUpdate_WhenConfirmationIsCancelled()
    {
        var appUpdateService = new FakeAppUpdateService
        {
            Result = new AppUpdateCheckResult(
                true,
                "1.0.0",
                "1.0.1",
                null,
                "https://github.com/Luofaiz/IGoLibrary/releases/latest/download/IGoLibrarySetup.exe",
                "abc123",
                "https://github.com/Luofaiz/IGoLibrary/releases/latest")
        };
        var confirmationDialogService = new FakeConfirmationDialogService
        {
            NextResult = false
        };
        var viewModel = CreateViewModel(
            confirmationDialogService: confirmationDialogService,
            appUpdateService: appUpdateService);

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, appUpdateService.CheckCalls);
        Assert.Equal(0, appUpdateService.InstallCalls);
        Assert.Single(confirmationDialogService.Requests);
    }

    [Fact]
    public async Task CheckForUpdatesOnStartupAsync_ChecksWithoutShowingUpToDateToast()
    {
        var notifications = new FakeNotificationService();
        var appUpdateService = new FakeAppUpdateService();
        var viewModel = CreateViewModel(
            notificationService: notifications,
            appUpdateService: appUpdateService);

        await viewModel.CheckForUpdatesOnStartupAsync();

        Assert.Equal(1, appUpdateService.CheckCalls);
        Assert.Empty(notifications.Infos);
    }

    [Fact]
    public async Task CheckForUpdatesOnStartupAsync_UsesSameUpdatePromptWhenUpdateIsAvailable()
    {
        var appUpdateService = new FakeAppUpdateService
        {
            Result = new AppUpdateCheckResult(
                true,
                "1.0.0",
                "1.0.1",
                "启动检查测试",
                "https://github.com/Luofaiz/IGoLibrary/releases/latest/download/IGoLibrarySetup.exe",
                "abc123",
                "https://github.com/Luofaiz/IGoLibrary/releases/latest")
        };
        var confirmationDialogService = new FakeConfirmationDialogService { NextResult = false };
        var viewModel = CreateViewModel(
            confirmationDialogService: confirmationDialogService,
            appUpdateService: appUpdateService);

        await viewModel.CheckForUpdatesOnStartupAsync();

        Assert.Equal(1, appUpdateService.CheckCalls);
        Assert.Single(confirmationDialogService.Requests);
    }

    private static MainWindowViewModel CreateViewModel(
        FakeSessionService? sessionService = null,
        FakeLibraryService? libraryService = null,
        FakeSettingsService? settingsService = null,
        FakeTraceIntApiClient? apiClient = null,
        FakeGrabSeatCoordinator? grabSeatCoordinator = null,
        FakeTomorrowReservationCoordinator? tomorrowReservationCoordinator = null,
        FakeOccupySeatCoordinator? occupySeatCoordinator = null,
        FakeNotificationService? notificationService = null,
        FakeTaskAlertService? taskAlertService = null,
        FakeErrorDialogService? errorDialogService = null,
        FakeConfirmationDialogService? confirmationDialogService = null,
        FakeAppThemeService? appThemeService = null,
        FakeDailyCheckoutTaskScheduler? dailyCheckoutTaskScheduler = null,
        FakeAppUpdateService? appUpdateService = null)
    {
        return new MainWindowViewModel(
            sessionService ?? new FakeSessionService(),
            libraryService ?? new FakeLibraryService(),
            apiClient ?? new FakeTraceIntApiClient(),
            settingsService ?? new FakeSettingsService(AppSettings.Default),
            new FakeProtocolTemplateStore(new ProtocolTemplateSet("", "", "", "", "", "", "")),
            grabSeatCoordinator ?? new FakeGrabSeatCoordinator(),
            tomorrowReservationCoordinator ?? new FakeTomorrowReservationCoordinator(),
            occupySeatCoordinator ?? new FakeOccupySeatCoordinator(),
            taskAlertService ?? new FakeTaskAlertService(),
            new ActivityLogService(),
            notificationService ?? new FakeNotificationService(),
            errorDialogService ?? new FakeErrorDialogService(),
            confirmationDialogService ?? new FakeConfirmationDialogService(),
            appThemeService ?? new FakeAppThemeService(),
            new AppWindowService(),
            dailyCheckoutTaskScheduler ?? new FakeDailyCheckoutTaskScheduler(),
            appUpdateService ?? new FakeAppUpdateService());
    }

    private static string BuildAuthorizationCookie(DateTimeOffset expiresAt)
    {
        var header = Base64Url("""{"typ":"JWT","alg":"RS256"}""");
        var payload = Base64Url($$"""{"userId":37580434,"schId":20175,"expireAt":{{expiresAt.ToUnixTimeSeconds()}},"tag":"cookie-test"}""");
        return $"Authorization={header}.{payload}.signature; SERVERID=test-server|1777956374|1777956374";
    }

    private static string Base64Url(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static Color GetBrushColor(IBrush brush)
    {
        return Assert.IsType<SolidColorBrush>(brush).Color;
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not met within the expected time.");
    }
}
