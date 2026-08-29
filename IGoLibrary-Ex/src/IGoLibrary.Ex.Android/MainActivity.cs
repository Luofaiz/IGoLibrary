using System.Net;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using IGoLibrary.Ex.Application.Abstractions;
using IGoLibrary.Ex.Application.Services;
using IGoLibrary.Ex.Application.State;
using IGoLibrary.Ex.Domain.Enums;
using IGoLibrary.Ex.Domain.Helpers;
using IGoLibrary.Ex.Domain.Models;
using IGoLibrary.Ex.Infrastructure.Api;

namespace IGoLibrary.Ex.Android;

[Activity(Label = "@string/app_name", MainLauncher = true)]
public sealed class MainActivity : Activity
{
    private const string PreferencesName = "igolibrary-mobile";
    private const string WeChatPackageName = "com.tencent.mm";
    private const string WeChatLauncherActivityName = "com.tencent.mm.ui.LauncherUI";
    private const string WeChatScanShortcutExtraKey = "LauncherUI.From.Scaner.Shortcut";
    private const string WeChatSchemeUri = "weixin://";
    private const string AuthEntryUrl = "https://open.weixin.qq.com/connect/oauth2/authorize?appid=wx2996d437cd442527&redirect_uri=https%3A//wechat.v2.traceint.com/index.php/graphql%3FoperationName%3Dindex%26query%3Dquery%257BuserAuth%257BtongJi%257Brank%257D%257D%257D&response_type=code&scope=snsapi_userinfo&state=1&connect_redirect=1";

    private readonly List<LibrarySummary> _libraries = [];
    private readonly AppRuntimeState _runtimeState = new();
    private readonly MobileSettingsService _settingsService = new();
    private readonly IActivityLogService _activityLogService = new ActivityLogService();

    private ICredentialStore _credentialStore = null!;
    private INotificationService _notificationService = null!;
    private ITaskAlertService _taskAlertService = null!;
    private ISessionService _sessionService = null!;
    private ITraceIntApiClient _apiClient = null!;
    private IGrabSeatCoordinator _grabSeatCoordinator = null!;
    private ITomorrowReservationCoordinator _tomorrowReservationCoordinator = null!;
    private IOccupySeatCoordinator _occupySeatCoordinator = null!;
    private HttpClient _httpClient = null!;

    private EditText _authLinkInput = null!;
    private EditText _targetSeatsInput = null!;
    private EditText _scheduledTimeInput = null!;
    private EditText _occupyLeadMinutesInput = null!;
    private EditText _occupyLeadSecondsInput = null!;
    private EditText _occupyScheduledTimeInput = null!;
    private Button _openWechatButton = null!;
    private Button _copyAuthUrlButton = null!;
    private Button _loginButton = null!;
    private Button _loadLibrariesButton = null!;
    private Button _refreshSeatsButton = null!;
    private Button _refreshTomorrowSeatsButton = null!;
    private Button _startTodayGrabButton = null!;
    private Button _startTomorrowReservationButton = null!;
    private Button _startRandomGrabButton = null!;
    private Button _stopGrabButton = null!;
    private Button _refreshReservationButton = null!;
    private Button _cancelReservationButton = null!;
    private Button _cancelTomorrowReservationButton = null!;
    private Button _startOccupyButton = null!;
    private Button _stopOccupyButton = null!;
    private Spinner _librarySpinner = null!;
    private Spinner _grabModeSpinner = null!;
    private Spinner _reservationStrategySpinner = null!;
    private Spinner _occupyTriggerSpinner = null!;
    private Spinner _refreshModeSpinner = null!;
    private TextView _statusText = null!;
    private TextView _seatText = null!;
    private TextView _reservationText = null!;
    private TextView _grabTaskText = null!;
    private TextView _occupyTaskText = null!;
    private TextView _logText = null!;

    private string _cookie = string.Empty;
    private string _lastClipboardAuthCode = string.Empty;
    private LibraryLayout? _currentLayout;
    private LibraryLayout? _currentTomorrowLayout;
    private ReservationInfo? _currentReservation;
    private IReadOnlyList<ReservationRecord> _reservationRecords = [];
    private bool _busy;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        InitializeServices();
        BuildUi();
        RegisterUiEventHandlers();
        _ = RestoreSessionQuietlyAsync();
    }

    protected override void OnDestroy()
    {
        UnregisterUiEventHandlers();
        try
        {
            StopAllCoordinatorsAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _httpClient.Dispose();
        base.OnDestroy();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _ = TryImportAuthLinkFromClipboardAsync();
    }

    private void InitializeServices()
    {
        _credentialStore = new MobileCredentialStore(GetPreferences);
        _notificationService = new MobileNotificationService(this);
        _taskAlertService = new MobileTaskAlertService(_notificationService);

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/107.0 Mobile Safari/537.36 MicroMessenger/8.0");

        _apiClient = new TraceIntApiClient(
            _httpClient,
            new MobileProtocolTemplateStore(),
            _settingsService,
            _runtimeState,
            _credentialStore,
            _activityLogService);
        _sessionService = new SessionService(
            _apiClient,
            _credentialStore,
            _activityLogService,
            _runtimeState);
        _grabSeatCoordinator = new GrabSeatCoordinator(
            _apiClient,
            _settingsService,
            _taskAlertService,
            _activityLogService,
            _runtimeState);
        _tomorrowReservationCoordinator = new TomorrowReservationCoordinator(
            _apiClient,
            new PrereserveQueueClient(),
            _taskAlertService,
            _activityLogService,
            _runtimeState);
        _occupySeatCoordinator = new OccupySeatCoordinator(
            _apiClient,
            _settingsService,
            _notificationService,
            _taskAlertService,
            _activityLogService,
            _runtimeState);
    }

    private void RegisterUiEventHandlers()
    {
        _activityLogService.EntryWritten += OnActivityLogEntryWritten;
        _grabSeatCoordinator.StatusChanged += OnGrabStatusChanged;
        _tomorrowReservationCoordinator.StatusChanged += OnGrabStatusChanged;
        _occupySeatCoordinator.StatusChanged += OnOccupyStatusChanged;
    }

    private void UnregisterUiEventHandlers()
    {
        _activityLogService.EntryWritten -= OnActivityLogEntryWritten;
        _grabSeatCoordinator.StatusChanged -= OnGrabStatusChanged;
        _tomorrowReservationCoordinator.StatusChanged -= OnGrabStatusChanged;
        _occupySeatCoordinator.StatusChanged -= OnOccupyStatusChanged;
    }

    private void BuildUi()
    {
        var root = new ScrollView(this)
        {
            FillViewport = true
        };

        var content = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        content.SetPadding(Dp(18), Dp(18), Dp(18), Dp(28));
        root.AddView(content, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        content.AddView(CreateText("IGoLibrary Android", 24, TypefaceStyle.Bold));
        content.AddView(CreateText("点击微信授权或扫码，拿到包含 code 的授权链接后回到本 App 自动登录。", 14, TypefaceStyle.Normal));

        var authActions = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        _openWechatButton = CreateButton("打开微信授权");
        _copyAuthUrlButton = CreateButton("复制授权入口");
        _openWechatButton.Click += (_, _) => OpenWechatAuthorization();
        _copyAuthUrlButton.Click += (_, _) => CopyAuthorizationEntryUrl();
        authActions.AddView(_openWechatButton, WeightWrap());
        authActions.AddView(_copyAuthUrlButton, WeightWrap(left: 10));
        content.AddView(authActions, MatchWrap(top: 16));

        var qrImage = new ImageView(this);
        qrImage.SetAdjustViewBounds(true);
        qrImage.SetImageResource(Resource.Drawable.qrcode);
        qrImage.SetBackgroundColor(Color.White);
        qrImage.SetPadding(Dp(14), Dp(14), Dp(14), Dp(14));
        qrImage.SetScaleType(ImageView.ScaleType.FitCenter);
        qrImage.Click += (_, _) => CopyAuthorizationEntryUrl();
        content.AddView(qrImage, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            Dp(260))
        {
            TopMargin = Dp(14)
        });

        content.AddView(
            CreateText("微信授权完成后，复制打开后的链接并回到这里；App 会自动从剪贴板解析。也可以手动粘贴链接后点击解析。", 13, TypefaceStyle.Normal),
            MatchWrap(top: 10));

        _authLinkInput = new EditText(this)
        {
            Hint = "粘贴包含 code 的授权链接；Cookie 仅作备用"
        };
        _authLinkInput.SetMinLines(3);
        _authLinkInput.SetMaxLines(6);
        _authLinkInput.SetSingleLine(false);
        _authLinkInput.InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextFlagMultiLine;
        content.AddView(_authLinkInput, MatchWrap(top: 12));

        _loginButton = CreateButton("解析授权链接登录");
        _loginButton.Click += async (_, _) => await RunBusyAsync("授权登录", LoginAsync);
        content.AddView(_loginButton, MatchWrap(top: 12));

        content.AddView(CreateSectionTitle("场馆与座位"), MatchWrap(top: 18));
        _loadLibrariesButton = CreateButton("加载场馆");
        _loadLibrariesButton.Click += async (_, _) => await RunBusyAsync("加载场馆", LoadLibrariesAsync);
        content.AddView(_loadLibrariesButton, MatchWrap(top: 10));

        _librarySpinner = new Spinner(this);
        content.AddView(_librarySpinner, MatchWrap(top: 10));

        _refreshSeatsButton = CreateButton("刷新今日座位");
        _refreshTomorrowSeatsButton = CreateButton("刷新明日座位");
        _refreshSeatsButton.Click += async (_, _) => await RunBusyAsync("刷新今日座位", RefreshSeatsAsync);
        _refreshTomorrowSeatsButton.Click += async (_, _) => await RunBusyAsync("刷新明日座位", RefreshTomorrowSeatsAsync);
        AddButtonRow(content, 10, _refreshSeatsButton, _refreshTomorrowSeatsButton);

        _targetSeatsInput = new EditText(this)
        {
            Hint = "目标座位号或 seat key，多个用逗号分隔，例如 101, 102"
        };
        _targetSeatsInput.SetSingleLine(false);
        _targetSeatsInput.SetMinLines(2);
        _targetSeatsInput.SetMaxLines(4);
        _targetSeatsInput.InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextFlagMultiLine;
        content.AddView(_targetSeatsInput, MatchWrap(top: 10));

        content.AddView(CreateText("预约模式", 13, TypefaceStyle.Bold), MatchWrap(top: 12));
        _grabModeSpinner = new Spinner(this);
        _grabModeSpinner.Adapter = CreateAdapter(["极限速度", "随机延迟", "延迟 3 秒"]);
        _grabModeSpinner.SetSelection((int)GrabMode.Relaxed);
        content.AddView(_grabModeSpinner, MatchWrap(top: 6));

        content.AddView(CreateText("今日抢座策略", 13, TypefaceStyle.Bold), MatchWrap(top: 10));
        _reservationStrategySpinner = new Spinner(this);
        _reservationStrategySpinner.Adapter = CreateAdapter(["先查空座再预约", "直接预约看返回值"]);
        _reservationStrategySpinner.SetSelection((int)GrabReservationStrategy.QueryThenReserve);
        content.AddView(_reservationStrategySpinner, MatchWrap(top: 6));

        _scheduledTimeInput = new EditText(this)
        {
            Hint = "定时开始 HH:mm:ss；留空或 00:00:00 表示立即",
            Text = "00:00:00"
        };
        _scheduledTimeInput.InputType = global::Android.Text.InputTypes.ClassDatetime;
        content.AddView(_scheduledTimeInput, MatchWrap(top: 10));

        _startTodayGrabButton = CreateButton("开始今日抢座");
        _startTomorrowReservationButton = CreateButton("开始明日预约");
        _startTodayGrabButton.Click += async (_, _) => await RunBusyAsync("启动今日抢座", StartTodayGrabAsync);
        _startTomorrowReservationButton.Click += async (_, _) => await RunBusyAsync("启动明日预约", StartTomorrowReservationAsync);
        AddButtonRow(content, 12, _startTodayGrabButton, _startTomorrowReservationButton);

        _startRandomGrabButton = CreateButton("一键随机抢座");
        _stopGrabButton = CreateButton("停止抢座/预约");
        _startRandomGrabButton.Click += async (_, _) => await RunBusyAsync("启动随机抢座", StartRandomAvailableSeatGrabAsync);
        _stopGrabButton.Click += async (_, _) => await RunBusyAsync("停止抢座/预约", StopGrabAsync);
        AddButtonRow(content, 10, _startRandomGrabButton, _stopGrabButton);

        _grabTaskText = CreatePanelText("抢座任务：未运行");
        content.AddView(_grabTaskText, MatchWrap(top: 10));

        _seatText = CreatePanelText("座位信息：未加载");
        content.AddView(_seatText, MatchWrap(top: 10));

        content.AddView(CreateSectionTitle("预约与占座"), MatchWrap(top: 18));
        _refreshReservationButton = CreateButton("当前预约");
        _cancelReservationButton = CreateButton("取消今日预约");
        _cancelTomorrowReservationButton = CreateButton("取消明日预约");
        _refreshReservationButton.Click += async (_, _) => await RunBusyAsync("查询预约", RefreshReservationAsync);
        _cancelReservationButton.Click += async (_, _) =>
            await RunBusyAsync(_currentReservation?.IsCheckedIn == true ? "退座" : "取消今日预约", CancelReservationAsync);
        _cancelTomorrowReservationButton.Click += async (_, _) => await RunBusyAsync("取消明日预约", CancelTomorrowReservationAsync);
        AddButtonRow(content, 10, _refreshReservationButton, _cancelReservationButton);
        content.AddView(_cancelTomorrowReservationButton, MatchWrap(top: 10));

        _reservationText = CreatePanelText("预约信息：未查询");
        content.AddView(_reservationText, MatchWrap(top: 10));

        content.AddView(CreateText("占座触发方式", 13, TypefaceStyle.Bold), MatchWrap(top: 12));
        _occupyTriggerSpinner = new Spinner(this);
        _occupyTriggerSpinner.Adapter = CreateAdapter(["到期前", "指定时间"]);
        content.AddView(_occupyTriggerSpinner, MatchWrap(top: 6));

        var occupyLeadRow = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        _occupyLeadMinutesInput = new EditText(this)
        {
            Hint = "提前分钟",
            Text = "1"
        };
        _occupyLeadSecondsInput = new EditText(this)
        {
            Hint = "提前秒",
            Text = "0"
        };
        _occupyLeadMinutesInput.InputType = global::Android.Text.InputTypes.ClassNumber;
        _occupyLeadSecondsInput.InputType = global::Android.Text.InputTypes.ClassNumber;
        occupyLeadRow.AddView(_occupyLeadMinutesInput, WeightWrap());
        occupyLeadRow.AddView(_occupyLeadSecondsInput, WeightWrap(left: 10));
        content.AddView(occupyLeadRow, MatchWrap(top: 10));

        _occupyScheduledTimeInput = new EditText(this)
        {
            Hint = "指定重约时间 HH:mm:ss",
            Text = "00:00:00"
        };
        _occupyScheduledTimeInput.InputType = global::Android.Text.InputTypes.ClassDatetime;
        content.AddView(_occupyScheduledTimeInput, MatchWrap(top: 10));

        content.AddView(CreateText("占座刷新频率", 13, TypefaceStyle.Bold), MatchWrap(top: 10));
        _refreshModeSpinner = new Spinner(this);
        _refreshModeSpinner.Adapter = CreateAdapter(["固定 10 秒", "随机 10-20 秒"]);
        content.AddView(_refreshModeSpinner, MatchWrap(top: 6));

        _startOccupyButton = CreateButton("开始占座");
        _stopOccupyButton = CreateButton("停止占座");
        _startOccupyButton.Click += async (_, _) => await RunBusyAsync("启动占座", StartOccupyAsync);
        _stopOccupyButton.Click += async (_, _) => await RunBusyAsync("停止占座", StopOccupyAsync);
        AddButtonRow(content, 12, _startOccupyButton, _stopOccupyButton);

        _occupyTaskText = CreatePanelText("占座任务：未运行");
        content.AddView(_occupyTaskText, MatchWrap(top: 10));

        _statusText = CreatePanelText("等待登录。");
        content.AddView(_statusText, MatchWrap(top: 18));

        content.AddView(CreateSectionTitle("运行日志"), MatchWrap(top: 18));
        _logText = CreatePanelText(string.Empty);
        _logText.SetMinLines(10);
        content.AddView(_logText, MatchWrap(top: 10));

        SetContentView(root);
        RefreshButtonState();
    }

    private async Task RestoreSessionQuietlyAsync()
    {
        _busy = true;
        RefreshButtonState();
        try
        {
            var session = await _sessionService.RestoreAsync();
            if (session is null)
            {
                SetStatus("等待登录。");
                return;
            }

            _cookie = session.Cookie;
            _authLinkInput.Text = session.Cookie;
            SetStatus("已恢复本地会话。可直接加载场馆；如失败请重新微信授权。");
        }
        catch (Exception ex)
        {
            SetStatus($"恢复本地会话失败：{ex.Message}");
            AppendLog($"恢复本地会话失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RefreshButtonState();
        }
    }

    private async Task LoginAsync()
    {
        HideKeyboard();
        var input = _authLinkInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("请先粘贴微信授权后复制的链接。");
        }

        SessionCredentials session;
        if (CodeLinkParser.TryExtractCode(input, out var code))
        {
            AppendLog("检测到授权链接，正在换取并验证 Cookie...");
            session = await _sessionService.AuthenticateFromCodeAsync(code);
        }
        else if (LooksLikeCookie(input))
        {
            AppendLog("检测到手动 Cookie，正在验证...");
            session = await _sessionService.AuthenticateFromCookieAsync(input, remember: true);
        }
        else
        {
            throw new InvalidOperationException("未从文本中找到 32 位 code。请用微信授权后复制完整链接；Cookie 只作为备用登录方式。");
        }

        _cookie = session.Cookie;
        _authLinkInput.Text = session.Cookie;
        SetStatus("登录验证成功。");
        RefreshButtonState();
    }

    private void OpenWechatAuthorization()
    {
        var copied = CopyAuthorizationEntryUrlToClipboard();

        if (TryOpenAuthorizationEntryInWechat())
        {
            SetStatus("已请求微信打开授权入口。授权完成后复制包含 code 的链接，再回到本 App 登录。");
            AppendLog("已将微信授权入口交给微信打开。");
            return;
        }

        if (TryShareAuthorizationEntryToWechat())
        {
            SetStatus(copied
                ? "已打开微信发送界面。请把授权入口发送给文件传输助手或任意聊天，点开链接完成授权。"
                : "已打开微信发送界面，但复制授权入口失败。请发送后点开链接完成授权。");
            AppendLog("已打开微信发送界面并附带授权入口。");
            return;
        }

        if (TryOpenWechatByScheme() ||
            TryOpenWechatLauncherShortcut() ||
            TryOpenWechatLauncherByPackage() ||
            TryOpenWechatLauncher())
        {
            SetStatus(copied
                ? "微信已打开，授权入口已复制。请在微信里粘贴访问，授权后复制包含 code 的链接回到本 App。"
                : "微信已打开。授权入口复制失败，请回到 App 手动复制后再试。");
            AppendLog("已直接打开微信客户端。");
            return;
        }

        SetStatus(copied
            ? "未检测到可打开的微信客户端。授权入口已复制，请安装/打开微信后粘贴访问。"
            : "未检测到可打开的微信客户端，且复制授权入口失败。");
        AppendLog("未能打开微信客户端。");
    }

    private void CopyAuthorizationEntryUrl()
    {
        if (CopyAuthorizationEntryUrlToClipboard())
        {
            SetStatus("授权入口已复制。可以粘贴到微信里打开。");
            AppendLog("已复制微信授权入口。");
            return;
        }

        SetStatus("复制授权入口失败，请检查系统剪贴板权限。");
        AppendLog("复制微信授权入口失败。");
    }

    private bool CopyAuthorizationEntryUrlToClipboard()
    {
        var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
        if (clipboard is null)
        {
            return false;
        }

        clipboard.PrimaryClip = ClipData.NewPlainText("IGoLibrary 微信授权入口", AuthEntryUrl);
        return true;
    }

    private bool TryOpenAuthorizationEntryInWechat()
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(AuthEntryUrl));
            intent.AddCategory(Intent.CategoryBrowsable);
            intent.SetPackage(WeChatPackageName);
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
            return true;
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException or InvalidOperationException)
        {
            AppendLog($"微信未接管授权链接：{ex.Message}");
            return false;
        }
    }

    private bool TryOpenWechatLauncherShortcut()
    {
        try
        {
            var intent = new Intent(Intent.ActionMain);
            intent.AddCategory(Intent.CategoryLauncher);
            intent.SetComponent(new ComponentName(WeChatPackageName, WeChatLauncherActivityName));
            intent.PutExtra(WeChatScanShortcutExtraKey, true);
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
            return true;
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException or InvalidOperationException)
        {
            AppendLog($"打开微信主界面失败：{ex.Message}");
            return false;
        }
    }

    private bool TryOpenWechatByScheme()
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(WeChatSchemeUri));
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
            return true;
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException or InvalidOperationException)
        {
            AppendLog($"通过 weixin:// 打开微信失败：{ex.Message}");
            return false;
        }
    }

    private bool TryOpenWechatLauncherByPackage()
    {
        try
        {
            var intent = new Intent(Intent.ActionMain);
            intent.AddCategory(Intent.CategoryLauncher);
            intent.SetPackage(WeChatPackageName);
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
            return true;
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException or InvalidOperationException)
        {
            AppendLog($"通过微信包名打开微信失败：{ex.Message}");
            return false;
        }
    }

    private bool TryOpenWechatLauncher()
    {
        try
        {
            var launchIntent = PackageManager?.GetLaunchIntentForPackage(WeChatPackageName);
            if (launchIntent is null)
            {
                return false;
            }

            StartActivity(launchIntent);
            return true;
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException or InvalidOperationException)
        {
            AppendLog($"打开微信客户端失败：{ex.Message}");
            return false;
        }
    }

    private bool TryShareAuthorizationEntryToWechat()
    {
        try
        {
            var intent = new Intent(Intent.ActionSend);
            intent.SetType("text/plain");
            intent.SetPackage(WeChatPackageName);
            intent.PutExtra(Intent.ExtraText, AuthEntryUrl);
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
            return true;
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException or InvalidOperationException)
        {
            AppendLog($"分享授权入口到微信失败：{ex.Message}");
            return false;
        }
    }

    private async Task TryImportAuthLinkFromClipboardAsync()
    {
        if (_busy || _authLinkInput is null)
        {
            return;
        }

        var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
        var clip = clipboard?.PrimaryClip;
        if (clip is null || clip.ItemCount == 0)
        {
            return;
        }

        var text = clip.GetItemAt(0)?.CoerceToText(this)?.ToString();
        if (!CodeLinkParser.TryExtractCode(text, out var code) ||
            string.Equals(code, _lastClipboardAuthCode, StringComparison.Ordinal))
        {
            return;
        }

        _lastClipboardAuthCode = code;
        _authLinkInput.Text = text;
        AppendLog("检测到剪贴板中的授权链接，正在自动登录...");
        await RunBusyAsync("授权登录", LoginAsync);
    }

    private async Task LoadLibrariesAsync()
    {
        var cookie = GetCurrentCookieOrThrow();
        var libraries = await _apiClient.GetLibrariesAsync(cookie);
        _libraries.Clear();
        _libraries.AddRange(libraries);
        _runtimeState.Libraries = _libraries.ToArray();

        var labels = _libraries.Count == 0
            ? ["没有可用场馆"]
            : _libraries
                .Select(library => $"{library.Name} / {library.Floor} / 空闲约 {Math.Max(0, library.SeatsTotal - library.SeatsUsed - library.SeatsBooking)}")
                .ToArray();

        _librarySpinner.Adapter = CreateAdapter(labels);
        _currentLayout = null;
        _currentTomorrowLayout = null;
        _seatText.Text = $"已加载 {_libraries.Count} 个场馆。";
        RefreshButtonState();
    }

    private async Task RefreshSeatsAsync()
    {
        var cookie = GetCurrentCookieOrThrow();
        var library = GetSelectedLibrary();
        _runtimeState.BoundLibrary = library;
        _currentLayout = await _apiClient.GetLibraryLayoutAsync(cookie, library.LibraryId);
        _runtimeState.CurrentLayout = _currentLayout;
        _seatText.Text = BuildSeatSummary("今日座位", _currentLayout);
    }

    private async Task RefreshTomorrowSeatsAsync()
    {
        var cookie = GetCurrentCookieOrThrow();
        var library = GetSelectedLibrary();
        _runtimeState.BoundLibrary = library;
        _currentTomorrowLayout = await _apiClient.GetPrereserveLibraryLayoutAsync(cookie, library.LibraryId);
        _currentTomorrowLayout = _currentTomorrowLayout with
        {
            Name = library.Name,
            Floor = library.Floor
        };
        _runtimeState.CurrentLayout = _currentTomorrowLayout;
        _seatText.Text = BuildSeatSummary("明日座位", _currentTomorrowLayout);
    }

    private async Task StartTodayGrabAsync()
    {
        var library = GetSelectedLibrary();
        var seats = await ResolveTargetSeatsAsync(useTomorrowLayout: false);
        await PersistGrabReservationStrategyAsync();

        var mode = GetSelectedGrabMode();
        var plan = new GrabSeatPlan(
            library.LibraryId,
            library.Name,
            seats,
            mode,
            GrabStrategyFactory.FromMode(mode),
            ParseScheduledTime());
        await _grabSeatCoordinator.StartAsync(plan);
    }

    private async Task StartTomorrowReservationAsync()
    {
        var library = GetSelectedLibrary();
        var seats = await ResolveTargetSeatsAsync(useTomorrowLayout: true);
        var mode = GetSelectedGrabMode();
        var plan = new TomorrowReservationPlan(
            library.LibraryId,
            library.Name,
            seats,
            mode,
            GrabStrategyFactory.FromMode(mode),
            ParseScheduledTime());
        await _tomorrowReservationCoordinator.StartAsync(plan);
    }

    private async Task StartRandomAvailableSeatGrabAsync()
    {
        var library = GetSelectedLibrary();
        await PersistGrabReservationStrategyAsync();

        var mode = GetSelectedGrabMode();
        var plan = new GrabSeatPlan(
            library.LibraryId,
            library.Name,
            [],
            mode,
            GrabStrategyFactory.FromMode(mode),
            ParseScheduledTime(),
            UseRandomAvailableSeat: true);
        await _grabSeatCoordinator.StartAsync(plan);
    }

    private async Task StopGrabAsync()
    {
        await _grabSeatCoordinator.StopAsync();
        await _tomorrowReservationCoordinator.StopAsync();
    }

    private async Task RefreshReservationAsync()
    {
        var cookie = GetCurrentCookieOrThrow();
        _reservationRecords = await _apiClient.GetReservationRecordsAsync(cookie);
        _runtimeState.ReservationRecords = _reservationRecords;
        _currentReservation = BuildTodayReservationInfo(_reservationRecords);
        _runtimeState.CurrentReservation = _currentReservation;
        _cancelReservationButton.Text = _currentReservation?.IsCheckedIn == true ? "退座" : "取消今日预约";
        _reservationText.Text = BuildReservationSummary(_reservationRecords);
    }

    private async Task CancelReservationAsync()
    {
        var cookie = GetCurrentCookieOrThrow();
        if (_currentReservation is null)
        {
            await RefreshReservationAsync();
        }

        var reservation = _currentReservation ?? throw new InvalidOperationException("当前没有可取消的今日预约。");
        if (IsTaskActive(_occupySeatCoordinator.GetStatus()))
        {
            await _occupySeatCoordinator.StopAsync();
        }

        var ok = await _apiClient.CancelReservationAsync(cookie, reservation.ReservationToken);
        if (!ok)
        {
            throw new InvalidOperationException("取消今日预约失败。");
        }

        _currentReservation = null;
        await RefreshReservationAsync();
    }

    private async Task CancelTomorrowReservationAsync()
    {
        var cookie = GetCurrentCookieOrThrow();
        if (IsTaskActive(_tomorrowReservationCoordinator.GetStatus()))
        {
            await _tomorrowReservationCoordinator.StopAsync();
        }

        var ok = await _apiClient.CancelPrereserveAsync(cookie);
        if (!ok)
        {
            throw new InvalidOperationException("取消明日预约失败。");
        }

        await RefreshReservationAsync();
    }

    private async Task StartOccupyAsync()
    {
        GetCurrentCookieOrThrow();
        var plan = BuildOccupySeatPlan();
        await _occupySeatCoordinator.StartAsync(plan);
    }

    private async Task StopOccupyAsync()
    {
        await _occupySeatCoordinator.StopAsync();
    }

    private async Task StopAllCoordinatorsAsync()
    {
        await _grabSeatCoordinator.StopAsync();
        await _tomorrowReservationCoordinator.StopAsync();
        await _occupySeatCoordinator.StopAsync();
    }

    private async Task<IReadOnlyList<TrackedSeat>> ResolveTargetSeatsAsync(bool useTomorrowLayout)
    {
        var library = GetSelectedLibrary();
        var layout = await GetLayoutForTargetResolutionAsync(library, useTomorrowLayout);
        var tokens = ParseTargetSeatTokens();
        if (tokens.Length == 0)
        {
            throw new InvalidOperationException("请先输入至少一个目标座位号或 seat key。");
        }

        var selected = new List<TrackedSeat>();
        var missing = new List<string>();
        foreach (var token in tokens)
        {
            var seat = layout.Seats.FirstOrDefault(candidate =>
                string.Equals(candidate.SeatKey, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.SeatName, token, StringComparison.OrdinalIgnoreCase));
            if (seat is null)
            {
                missing.Add(token);
                continue;
            }

            if (selected.Any(item => string.Equals(item.SeatKey, seat.SeatKey, StringComparison.Ordinal)))
            {
                continue;
            }

            selected.Add(new TrackedSeat(seat.SeatKey, seat.SeatName));
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"未在当前场馆座位图中找到：{string.Join(", ", missing)}。请先刷新座位，或确认输入的是座位号/key。");
        }

        if (selected.Count == 0)
        {
            throw new InvalidOperationException("没有可用的目标座位。");
        }

        _targetSeatsInput.Text = string.Join(", ", selected.Select(seat => seat.SeatName));
        return selected;
    }

    private async Task<LibraryLayout> GetLayoutForTargetResolutionAsync(LibrarySummary library, bool useTomorrowLayout)
    {
        if (useTomorrowLayout)
        {
            if (_currentTomorrowLayout?.LibraryId == library.LibraryId)
            {
                return _currentTomorrowLayout;
            }

            try
            {
                await RefreshTomorrowSeatsAsync();
                return _currentTomorrowLayout ?? throw new InvalidOperationException("明日座位布局未加载。");
            }
            catch (Exception ex) when (_currentLayout?.LibraryId == library.LibraryId)
            {
                AppendLog($"刷新明日座位失败，使用今日座位图匹配目标座位：{ex.Message}");
                return _currentLayout;
            }
        }

        if (_currentLayout?.LibraryId != library.LibraryId)
        {
            await RefreshSeatsAsync();
        }

        return _currentLayout ?? throw new InvalidOperationException("今日座位布局未加载。");
    }

    private string[] ParseTargetSeatTokens()
    {
        var text = _targetSeatsInput.Text ?? string.Empty;
        return text.Split(
                [',', '，', ';', '；', ' ', '\n', '\r', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task PersistGrabReservationStrategyAsync()
    {
        var settings = await _settingsService.LoadAsync();
        var strategy = (GrabReservationStrategy)Math.Clamp(
            _reservationStrategySpinner.SelectedItemPosition,
            (int)GrabReservationStrategy.QueryThenReserve,
            (int)GrabReservationStrategy.ReserveDirectly);
        await _settingsService.SaveAsync(settings with
        {
            GrabReservationStrategy = strategy
        });
    }

    private OccupySeatPlan BuildOccupySeatPlan()
    {
        var triggerMode = _occupyTriggerSpinner.SelectedItemPosition == 1
            ? OccupyReReserveTriggerMode.ScheduledTime
            : OccupyReReserveTriggerMode.BeforeExpiration;
        var minutes = Math.Clamp(ReadInt(_occupyLeadMinutesInput, 1), 0, 180);
        var seconds = Math.Clamp(ReadInt(_occupyLeadSecondsInput, 0), 0, 59);
        var leadTime = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        if (leadTime <= TimeSpan.Zero)
        {
            leadTime = TimeSpan.FromSeconds(1);
        }

        TimeOnly? scheduledTime = null;
        if (triggerMode == OccupyReReserveTriggerMode.ScheduledTime)
        {
            if (!TimeOnly.TryParse(_occupyScheduledTimeInput.Text, out var parsed))
            {
                throw new InvalidOperationException("指定重约时间格式应为 HH:mm:ss，例如 14:30:00。");
            }

            scheduledTime = parsed;
        }

        var refreshMode = _refreshModeSpinner.SelectedItemPosition == 1
            ? RefreshMode.RandomTenToTwentySeconds
            : RefreshMode.FixedTenSeconds;
        return new OccupySeatPlan(leadTime, refreshMode, triggerMode, scheduledTime);
    }

    private TimeOnly? ParseScheduledTime()
    {
        var text = _scheduledTimeInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text == "00:00:00")
        {
            return null;
        }

        return TimeOnly.TryParse(text, out var value)
            ? value
            : throw new InvalidOperationException("定时开始时间格式应为 HH:mm:ss，例如 08:00:00。");
    }

    private GrabMode GetSelectedGrabMode()
        => (GrabMode)Math.Clamp(_grabModeSpinner.SelectedItemPosition, (int)GrabMode.Aggressive, (int)GrabMode.Relaxed);

    private string GetCurrentCookieOrThrow()
    {
        var cookie = _runtimeState.Session?.Cookie ?? _cookie;
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new InvalidOperationException("请先验证登录。");
        }

        _cookie = cookie;
        return cookie;
    }

    private bool IsLoggedIn()
        => !string.IsNullOrWhiteSpace(_runtimeState.Session?.Cookie ?? _cookie);

    private LibrarySummary GetSelectedLibrary()
    {
        if (_libraries.Count == 0)
        {
            throw new InvalidOperationException("请先加载场馆。");
        }

        var index = Math.Clamp(_librarySpinner.SelectedItemPosition, 0, _libraries.Count - 1);
        return _libraries[index];
    }

    private void OnActivityLogEntryWritten(object? sender, AppLogEntry entry)
    {
        RunOnUiThread(() => AppendLog($"{entry.Category}：{entry.Message}"));
    }

    private void OnGrabStatusChanged(object? sender, CoordinatorStatus status)
    {
        RunOnUiThread(() =>
        {
            _grabTaskText.Text = FormatCoordinatorStatus(status);
            RefreshButtonState();
            if (status.State == CoordinatorTaskState.Completed)
            {
                _ = RefreshReservationAfterTaskAsync();
            }
        });
    }

    private void OnOccupyStatusChanged(object? sender, CoordinatorStatus status)
    {
        RunOnUiThread(() =>
        {
            _occupyTaskText.Text = FormatCoordinatorStatus(status);
            RefreshButtonState();
            if (status.State == CoordinatorTaskState.Completed)
            {
                _ = RefreshReservationAfterTaskAsync();
            }
        });
    }

    private async Task RefreshReservationAfterTaskAsync()
    {
        try
        {
            await RefreshReservationAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"任务结束后刷新预约失败：{ex.Message}");
        }
    }

    private async Task RunBusyAsync(string actionName, Func<Task> operation)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        RefreshButtonState();
        SetStatus($"{actionName}中...");
        try
        {
            await operation();
            if (!(_statusText.Text?.Contains("失败", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                SetStatus($"{actionName}完成。");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"{actionName}失败：{ex.Message}");
            AppendLog($"{actionName}失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RefreshButtonState();
        }
    }

    private void RefreshButtonState()
    {
        if (_openWechatButton is null)
        {
            return;
        }

        var loggedIn = IsLoggedIn();
        var hasLibraries = _libraries.Count > 0;
        var grabRunning = IsTaskActive(_grabSeatCoordinator.GetStatus()) ||
                          IsTaskActive(_tomorrowReservationCoordinator.GetStatus());
        var occupyRunning = IsTaskActive(_occupySeatCoordinator.GetStatus());

        _openWechatButton.Enabled = !_busy;
        _copyAuthUrlButton.Enabled = !_busy;
        _loginButton.Enabled = !_busy;
        _loadLibrariesButton.Enabled = !_busy && loggedIn && !grabRunning;
        _refreshSeatsButton.Enabled = !_busy && loggedIn && hasLibraries && !grabRunning;
        _refreshTomorrowSeatsButton.Enabled = !_busy && loggedIn && hasLibraries && !grabRunning;
        _startTodayGrabButton.Enabled = !_busy && loggedIn && hasLibraries && !grabRunning;
        _startTomorrowReservationButton.Enabled = !_busy && loggedIn && hasLibraries && !grabRunning;
        _startRandomGrabButton.Enabled = !_busy && loggedIn && hasLibraries && !grabRunning;
        _stopGrabButton.Enabled = !_busy && grabRunning;
        _refreshReservationButton.Enabled = !_busy && loggedIn;
        _cancelReservationButton.Enabled = !_busy && loggedIn;
        _cancelTomorrowReservationButton.Enabled = !_busy && loggedIn;
        _startOccupyButton.Enabled = !_busy && loggedIn && !occupyRunning;
        _stopOccupyButton.Enabled = !_busy && occupyRunning;

        UpdateKeepScreenOn(grabRunning || occupyRunning);
    }

    private void UpdateKeepScreenOn(bool enabled)
    {
        if (Window is null)
        {
            return;
        }

        if (enabled)
        {
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);
        }
        else
        {
            Window.ClearFlags(WindowManagerFlags.KeepScreenOn);
        }
    }

    private string BuildSeatSummary(string title, LibraryLayout layout)
    {
        var availableCount = layout.Seats.Count(seat => seat.IsAvailable);
        var samples = layout.Seats
            .OrderByDescending(seat => seat.IsAvailable)
            .ThenBy(seat => int.TryParse(seat.SeatName, out var number) ? number : int.MaxValue)
            .ThenBy(seat => seat.SeatName, StringComparer.Ordinal)
            .Take(60)
            .Select(seat => seat.IsAvailable ? seat.SeatName : $"{seat.SeatName}(占)")
            .ToArray();

        return
            $"{title}\n" +
            $"场馆：{layout.Name}\n" +
            $"楼层：{layout.Floor}\n" +
            $"开放：{(layout.IsOpen ? "是" : "否")}\n" +
            $"座位：总数 {layout.TotalSeats}，已预约 {layout.BookedSeats}，使用中 {layout.UsedSeats}，可用 {availableCount}\n" +
            $"座位示例：{(samples.Length == 0 ? "无" : string.Join(", ", samples))}";
    }

    private static ReservationInfo? BuildTodayReservationInfo(IEnumerable<ReservationRecord> records)
    {
        var today = records.FirstOrDefault(record =>
            record.Kind == ReservationRecordKind.Today &&
            record.ExpirationTime is not null);
        return today is null
            ? null
            : new ReservationInfo(
                today.ReservationToken,
                today.LibraryId,
                today.LibraryName,
                today.SeatKey,
                today.SeatName,
                today.ExpirationTime.GetValueOrDefault(),
                today.IsCheckedIn);
    }

    private static string BuildReservationSummary(IReadOnlyList<ReservationRecord> records)
    {
        if (records.Count == 0)
        {
            return "当前没有预约记录。";
        }

        return string.Join("\n\n", records.Select(record =>
        {
            var kind = record.Kind == ReservationRecordKind.Today ? "今日预约" : "明日预约";
            var time = record.Kind == ReservationRecordKind.Today && record.ExpirationTime is not null
                ? $"到期：{record.ExpirationTime.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
                : $"日期：{record.ReservationDate?.ToString("yyyy-MM-dd") ?? "明日"}";
            var state = record.Kind == ReservationRecordKind.Today
                ? $"签到：{(record.IsCheckedIn ? "已签到" : "未签到")}"
                : $"使用：{(record.IsUsed ? "已使用" : "未使用")}";
            return $"{kind}\n场馆：{record.LibraryName}\n座位：{record.SeatName}\n{time}\n{state}";
        }));
    }

    private static string FormatCoordinatorStatus(CoordinatorStatus status)
    {
        var state = status.State switch
        {
            CoordinatorTaskState.Starting => "启动中",
            CoordinatorTaskState.Running => "运行中",
            CoordinatorTaskState.Stopping => "停止中",
            CoordinatorTaskState.Completed => "已结束",
            CoordinatorTaskState.Failed => "失败",
            _ => "未运行"
        };
        var lastRequest = status.LastRequestAt is null
            ? "无"
            : status.LastRequestAt.Value.LocalDateTime.ToString("HH:mm:ss");
        return
            $"{status.Title}：{state}\n" +
            $"{status.Message}\n" +
            $"轮询：{status.PollCount}，请求：{status.RequestCount}，最近请求：{lastRequest}";
    }

    private static bool IsTaskActive(CoordinatorStatus status)
        => status.State is CoordinatorTaskState.Starting or CoordinatorTaskState.Running or CoordinatorTaskState.Stopping;

    private static int ReadInt(EditText input, int fallback)
        => int.TryParse(input.Text, out var value) ? value : fallback;

    private void SetStatus(string message)
    {
        _statusText.Text = message;
    }

    private void AppendLog(string message)
    {
        if (_logText is null)
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        var text = string.IsNullOrWhiteSpace(_logText.Text)
            ? line
            : $"{line}\n{_logText.Text}";
        var lines = text.Split('\n');
        _logText.Text = lines.Length <= 140
            ? text
            : string.Join('\n', lines.Take(140));
    }

    private ISharedPreferences GetPreferences()
        => GetSharedPreferences(PreferencesName, FileCreationMode.Private)!;

    private void HideKeyboard()
    {
        var inputMethodManager = (InputMethodManager?)GetSystemService(InputMethodService);
        inputMethodManager?.HideSoftInputFromWindow(_authLinkInput.WindowToken, HideSoftInputFlags.None);
    }

    private static bool LooksLikeCookie(string value)
        => value.Contains("Authorization=", StringComparison.OrdinalIgnoreCase) &&
           value.Contains("SERVERID=", StringComparison.OrdinalIgnoreCase);

    private TextView CreateText(string text, float size, TypefaceStyle style)
    {
        var view = new TextView(this)
        {
            Text = text,
            TextSize = size
        };
        view.SetTextColor(Color.Rgb(31, 41, 55));
        view.SetTypeface(Typeface.Default, style);
        return view;
    }

    private TextView CreateSectionTitle(string text)
        => CreateText(text, 18, TypefaceStyle.Bold);

    private TextView CreatePanelText(string text)
    {
        var view = CreateText(text, 14, TypefaceStyle.Normal);
        view.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        view.SetBackgroundColor(Color.Rgb(243, 244, 246));
        return view;
    }

    private Button CreateButton(string text)
    {
        var button = new Button(this)
        {
            Text = text
        };
        button.SetAllCaps(false);
        return button;
    }

    private ArrayAdapter<string> CreateAdapter(IReadOnlyList<string> labels)
    {
        var adapter = new ArrayAdapter<string>(
            this,
            global::Android.Resource.Layout.SimpleSpinnerItem,
            labels.ToArray());
        adapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
        return adapter;
    }

    private void AddButtonRow(LinearLayout content, int top, params Button[] buttons)
    {
        var row = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        for (var index = 0; index < buttons.Length; index++)
        {
            row.AddView(buttons[index], WeightWrap(left: index == 0 ? 0 : 10));
        }

        content.AddView(row, MatchWrap(top));
    }

    private LinearLayout.LayoutParams MatchWrap(int top = 0)
        => new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(top)
        };

    private LinearLayout.LayoutParams WeightWrap(int left = 0)
        => new(0, ViewGroup.LayoutParams.WrapContent, 1)
        {
            LeftMargin = Dp(left)
        };

    private int Dp(int value)
        => (int)(value * (Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);
}
