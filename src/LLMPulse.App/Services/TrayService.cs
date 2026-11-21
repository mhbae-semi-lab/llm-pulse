using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using LLMPulse.App.Localization;
using LLMPulse.Core;

namespace LLMPulse.App.Services;

/// <summary>트레이 아이콘, 닫기 시 트레이 상주, 변경 사항 토스트 알림.</summary>
public sealed class TrayService : IDisposable
{
    private readonly AppState _state;
    private readonly RefreshCoordinator _coordinator;
    private readonly Loc _loc = Loc.Instance;
    private TaskbarIcon? _icon;
    private Window? _window;
    private MenuItem? _open, _refresh, _quit, _widget;
    private readonly List<MenuItem> _plusItems = [];
    private Views.QuickPopup? _widgetWindow;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private System.Drawing.Icon? _baseIcon;
    private readonly Dictionary<string, System.Drawing.Icon> _statusIcons = [];
    private string _statusLevel = "";
    private bool _exiting;

    private readonly ViewModels.DashboardViewModel _dashboard;
    private Views.QuickPopup? _popup;

    public TrayService(AppState state, RefreshCoordinator coordinator, ViewModels.DashboardViewModel dashboard)
    {
        _state = state;
        _coordinator = coordinator;
        _dashboard = dashboard;
    }

    /// <summary>작은 요약 창을 토글한다 (이미 보이면 숨김).</summary>
    private void TogglePopup()
    {
        if (_popup is { IsVisible: true }) { _popup.Hide(); return; }
        _popup ??= new Views.QuickPopup(_dashboard, ShowWindow, () => _ = _coordinator.RunAsync(scheduled: false));
        _popup.Show();
        _popup.PlaceNearTray();
        _popup.Activate();
    }

    public void Attach(Window window)
    {
        _window = window;

        _open = new MenuItem();
        _refresh = new MenuItem();
        _quit = new MenuItem();
        _widget = new MenuItem();
        _widget.Click += (_, _) => ToggleWidget();
        _open.Click += (_, _) => ShowWindow();
        _refresh.Click += async (_, _) => await _coordinator.RunAsync(scheduled: false);
        _quit.Click += (_, _) => Exit();

        _icon = new TaskbarIcon
        {
            ToolTipText = "LLM Pulse",
            Icon = LoadIcon(),
            ContextMenu = new ContextMenu(),
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        RebuildMenu();
        _icon.TrayLeftMouseUp += (_, _) => TogglePopup();
        _icon.TrayMouseDoubleClick += (_, _) => { _popup?.Hide(); ShowWindow(); };
        _icon.ForceCreate(enablesEfficiencyMode: false);

        window.Closing += OnWindowClosing;
        _loc.LanguageChanged += UpdateTexts;
        _coordinator.Refreshed += OnRefreshed;
        _state.LimitsChanged += OnLimitsChanged;
        _state.DataChanged += OnLimitsChanged;
        RegisterHotkeys(window);
        UpdateTexts();
        UpdateStatus();
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        var tick = 0;
        _timer.Tick += async (_, _) =>
        {
            _state.RaiseLimitsChanged(); // 카운트다운 갱신
            if (++tick % 5 == 0) await _state.SyncLocalUsageAsync();
        };
        _timer.Start();
        _ = _state.SyncLocalUsageAsync();
    }

    public void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Exit()
    {
        _exiting = true;
        Application.Current.Shutdown();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || !_state.GetFlag(AppState.SettingTrayResident, false)) return;
        e.Cancel = true;
        _window?.Hide();
    }

    /// <summary>앱 리소스의 app.ico 를 트레이 아이콘으로 쓴다 (실패해도 앱은 계속 동작).</summary>
    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            return info is null ? null : new System.Drawing.Icon(info.Stream);
        }
        catch { return null; }
    }

    private void UpdateTexts()
    {
        if (_open is null) return;
        _open.Header = _loc["Tray_Open"];
        _refresh!.Header = _loc["Tray_Refresh"];
        _quit!.Header = _loc["Tray_Quit"];
        _widget!.Header = _loc["Tray_Widget"];
        UpdateStatus();
    }

    // ---- 전역 단축키: Ctrl+Alt+U 사용 +1 (가장 많이 쓴 구독), Ctrl+Alt+L 위젯 토글 ----
    private const int HotkeyPlus = 0x4C01, HotkeyWidget = 0x4C02;
    private const uint ModControlAlt = 0x0002 | 0x0001 | 0x4000; // CTRL | ALT | NOREPEAT
    private IntPtr _hwnd;
    private System.Windows.Interop.HwndSource? _source;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void RegisterHotkeys(Window window)
    {
        try
        {
            _hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            _source = System.Windows.Interop.HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(HotkeyHook);
            // 다른 앱이 이미 쓰고 있으면 등록만 실패하고 앱은 정상 동작
            RegisterHotKey(_hwnd, HotkeyPlus, ModControlAlt, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(System.Windows.Input.Key.U));
            RegisterHotKey(_hwnd, HotkeyWidget, ModControlAlt, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(System.Windows.Input.Key.L));
        }
        catch { /* 단축키는 부가 기능 */ }
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        if (wParam.ToInt32() == HotkeyPlus) QuickPlusOne();
        else if (wParam.ToInt32() == HotkeyWidget) ToggleWidget();
        return IntPtr.Zero;
    }

    /// <summary>한도가 있는 구독 중 사용률이 가장 높은 것에 +1 하고 토스트로 알린다.</summary>
    private void QuickPlusOne()
    {
        var target = _state.LimitInfos().OrderByDescending(i => i.Ratio).FirstOrDefault();
        if (target is null || _icon is null) return;
        var now = _state.AddUsage(target.Plan.Id, +1);
        if (now?.Cap is { } cap)
            _icon.ShowNotification(_loc["Widget_Usage"],
                $"{target.Plan.Name.Get(_loc.Language)}: {now.Used}/{cap}", NotificationIcon.Info);
    }

    private void ToggleWidget()
    {
        if (_widgetWindow is { IsLoaded: true }) { _widgetWindow.Close(); return; }
        _widgetWindow = new Views.QuickPopup(_dashboard, ShowWindow, () => _ = _coordinator.RunAsync(scheduled: false), pinned: true);
        _widgetWindow.Closed += (_, _) => _widgetWindow = null;
        _widgetWindow.Show();
        _widgetWindow.PlaceNearTray();
    }

    /// <summary>트레이 메뉴: 열기 / 구독별 +1 / 위젯 / 새로고침 / 종료.</summary>
    private void RebuildMenu()
    {
        if (_icon?.ContextMenu is not { } menu) return;
        menu.Items.Clear();
        menu.Items.Add(_open);
        var lang = _loc.Language;
        _plusItems.Clear();
        foreach (var info in _state.LimitInfos())
        {
            var planId = info.Plan.Id;
            var item = new MenuItem
            {
                Header = $"+1  {info.Plan.Name.Get(lang)}  ({info.Used.ToString("N0", _loc.Culture)}/{info.Cap.ToString("N0", _loc.Culture)})",
            };
            item.Click += (_, _) => _state.AddUsage(planId, +1);
            _plusItems.Add(item);
            menu.Items.Add(item);
        }

        menu.Items.Add(_widget);
        menu.Items.Add(_refresh);
        menu.Items.Add(new Separator());
        menu.Items.Add(_quit);
    }

    private void OnLimitsChanged()
    {
        RebuildMenu();
        UpdateStatus();
        NotifyLimits();
    }

    /// <summary>가장 높은 사용률에 따라 아이콘 점 색(없음/주의/소진)과 툴팁(남은 시간)을 갱신.</summary>
    private void UpdateStatus()
    {
        if (_icon is null) return;
        var infos = _state.LimitInfos();
        var worst = infos.OrderByDescending(i => i.Ratio).FirstOrDefault();
        var level = worst is null ? "none" : worst.Ratio >= 1 ? "full" : worst.Ratio >= 0.8 ? "warn" : "ok";

        if (level != _statusLevel)
        {
            _statusLevel = level;
            _icon.Icon = StatusIcon(level) ?? _icon.Icon;
        }

        var tip = "LLM Pulse";
        if (worst is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var reset = TimeText.ResetsIn(worst.ResetAt, now);
            tip += $"\n{worst.Plan.Name.Get(_loc.Language)}: {worst.Used}/{worst.Cap}" + (reset.Length > 0 ? $" · {reset}" : "");
        }

        _icon.ToolTipText = tip;
    }

    private System.Drawing.Icon? StatusIcon(string level)
    {
        _baseIcon ??= LoadIcon();
        if (_baseIcon is null || level is "none" or "ok") return _baseIcon;
        if (_statusIcons.TryGetValue(level, out var cached)) return cached;
        try
        {
            using var bmp = _baseIcon.ToBitmap();
            using var canvas = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(canvas))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, 32, 32);
                var color = level == "full" ? System.Drawing.Color.FromArgb(0xE5, 0x39, 0x35) : System.Drawing.Color.FromArgb(0xF5, 0xA6, 0x23);
                using var ring = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                using var dot = new System.Drawing.SolidBrush(color);
                g.FillEllipse(ring, 16, 16, 16, 16);
                g.FillEllipse(dot, 18, 18, 12, 12);
            }

            var hIcon = canvas.GetHicon();
            var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            _statusIcons[level] = icon;
            return icon;
        }
        catch { return _baseIcon; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>사용량이 80% / 100% 에 처음 도달했을 때 한 번만 알린다 (구독·기간 창마다).</summary>
    private void NotifyLimits()
    {
        if (_icon is null || !_state.GetFlag(AppState.SettingNotify, true) || !_state.GetFlag(AppState.SettingNotifyLimit, true)) return;
        var lang = _loc.Language;
        foreach (var info in _state.LimitInfos())
        {
            var level = info.Ratio >= 1 ? 100 : info.Ratio >= 0.8 ? 80 : 0;
            var key = $"limit_notified_{info.Plan.Id}";
            // 기간 창 식별: 다음 초기화 시각 + 단계. 창이 바뀌면 다시 알린다.
            var stamp = $"{info.ResetAt?.ToUnixTimeSeconds() ?? 0}:{level}";
            var prev = _state.ReadSetting(key);
            if (level == 0) { if (prev is not null) _state.SaveSetting(key, ""); continue; }
            if (prev == stamp) continue;
            // 같은 창에서 이미 100 을 알렸다면 80 으로 되돌아가도 재알림 없음
            if (prev is { Length: > 0 } && prev.StartsWith($"{info.ResetAt?.ToUnixTimeSeconds() ?? 0}:") && int.TryParse(prev.Split(':')[^1], out var prevLevel) && level < prevLevel) continue;
            _state.SaveSetting(key, stamp);

            var name = info.Plan.Name.Get(lang);
            var body = level == 100
                ? _loc.Format("Toast_LimitFull", name, info.Used, info.Cap)
                : _loc.Format("Toast_LimitWarn", name, (info.Ratio * 100).ToString("0", _loc.Culture), info.Used, info.Cap);
            _icon.ShowNotification(_loc["Toast_LimitTitle"], body, NotificationIcon.Warning);
        }
    }

    /// <summary>하루에 한 번만, 종류별(1위 교체·신규 모델 / 가격 변동)로 알린다.</summary>
    private void OnRefreshed(RefreshSummary? _)
    {
        if (_icon is null || !_state.GetFlag(AppState.SettingNotify, true)) return;
        if (_state.Snapshot is not { } snap) return;
        // 의도적으로 로컬 날짜: "사용자의 하루에 한 번" 알림 제한이다. 데이터(스냅샷/점수) 날짜는 전부 UTC.
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        if (_state.GetFlag(AppState.SettingNotifyTop, true)
            && (snap.TopChangeCount > 0 || snap.NewModelCount > 0)
            && _state.ReadSetting(AppState.SettingLastNotified) != today)
        {
            _state.SaveSetting(AppState.SettingLastNotified, today);
            var text = _loc.Format("Toast_Body",
                snap.TopChangeCount.ToString("N0", _loc.Culture),
                snap.NewModelCount.ToString("N0", _loc.Culture));
            _icon.ShowNotification(_loc["Toast_Title"], text, NotificationIcon.Info);
        }

        var priceCount = _state.PriceChanges.Count(p => !_state.Restricting || _state.InPlan(p.ModelId));
        if (_state.GetFlag(AppState.SettingNotifyPrice, true)
            && priceCount > 0
            && _state.ReadSetting(AppState.SettingLastNotifiedPrice) != today)
        {
            _state.SaveSetting(AppState.SettingLastNotifiedPrice, today);
            _icon.ShowNotification(_loc["Toast_PriceTitle"],
                _loc.Format("Toast_PriceBody", priceCount.ToString("N0", _loc.Culture)), NotificationIcon.Info);
        }
    }
    public void Dispose()
    {
        _timer?.Stop();
        _widgetWindow?.Close();
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyPlus);
            UnregisterHotKey(_hwnd, HotkeyWidget);
        }
        _source?.RemoveHook(HotkeyHook);
        _icon?.Dispose();
        _icon = null;
    }
}