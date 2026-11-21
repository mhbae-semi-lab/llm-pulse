using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;
using Wpf.Ui.Appearance;

namespace LLMPulse.App.ViewModels;

public sealed partial class NavItemViewModel : ObservableObject
{
    public required PageKey Key { get; init; }
    public required string TitleKey { get; init; }
    public required string Glyph { get; init; }
    [ObservableProperty] private string _title = "";
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Loc _loc = Loc.Instance;
    private readonly IReadOnlyDictionary<PageKey, object> _pages;

    public AppState State { get; }
    public RefreshCoordinator Refresher { get; }
    public MetricsViewModel Metrics { get; }
    public LeaderboardViewModel Leaderboard { get; }
    public WizardViewModel Wizard { get; }
    public DashboardViewModel Dashboard { get; }
    private readonly CategoryDetailViewModel _categoryDetail;
    private readonly CompareViewModel _compare;
    private readonly ModelDetailViewModel _modelDetail;
    public IReadOnlyList<LanguageOption> Languages => Loc.Languages;
    public ObservableCollection<NavItemViewModel> NavItems { get; }

    [ObservableProperty] private NavItemViewModel? _selectedNav;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private LanguageOption _selectedLanguage;
    [ObservableProperty] private string _freshness = "";
    [ObservableProperty] private bool _freshnessWarning;
    [ObservableProperty] private string _themeGlyph = "\uE708"; // 달 아이콘 (Segoe MDL2)

    public string SearchText
    {
        get => State.SearchText;
        set => State.SearchText = value;
    }

    public MainViewModel(
        AppState state,
        RefreshCoordinator refresher,
        DashboardViewModel dashboard,
        LeaderboardViewModel leaderboard,
        MetricsViewModel metrics,
        SettingsViewModel settings,
        CategoryDetailViewModel categoryDetail,
        CompareViewModel compare,
        ModelDetailViewModel modelDetail,
        HistoryViewModel history,
        MatchingViewModel matching,
        PlansViewModel plans,
        WizardViewModel wizard)
    {
        Wizard = wizard;
        Dashboard = dashboard;
        State = state;
        Refresher = refresher;
        Metrics = metrics;
        Leaderboard = leaderboard;
        _categoryDetail = categoryDetail;
        _compare = compare;
        _modelDetail = modelDetail;

        _pages = new Dictionary<PageKey, object>
        {
            [PageKey.Dashboard] = dashboard,
            [PageKey.Leaderboard] = leaderboard,
            [PageKey.Metrics] = metrics,
            [PageKey.Matching] = matching,
            [PageKey.History] = history,
            [PageKey.Plans] = plans,
            [PageKey.Settings] = settings,
            [PageKey.About] = new AboutViewModel(),
        };

        NavItems =
        [
            new() { Key = PageKey.Dashboard, TitleKey = "Nav_Dashboard", Glyph = "\uE80F" },
            new() { Key = PageKey.Leaderboard, TitleKey = "Nav_Leaderboard", Glyph = "\uE9D2" },
            new() { Key = PageKey.Metrics, TitleKey = "Nav_Metrics", Glyph = "\uE946" },
            new() { Key = PageKey.History, TitleKey = "Nav_History", Glyph = "\uE81C" },
            new() { Key = PageKey.Plans, TitleKey = "Nav_Plans", Glyph = "\uE716" },
            new() { Key = PageKey.Matching, TitleKey = "Nav_Matching", Glyph = "\uE71B" },
            new() { Key = PageKey.Settings, TitleKey = "Nav_Settings", Glyph = "\uE713" },
            new() { Key = PageKey.About, TitleKey = "Nav_About", Glyph = "\uE783" },
        ];

        var saved = state.ReadSetting(AppState.SettingLanguage) ?? "en";
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == saved) ?? Languages[0];
        _loc.SetLanguage(_selectedLanguage.Code);

        ApplySavedTheme();
        RefreshTitles();

        _loc.LanguageChanged += () => { RefreshTitles(); UpdateFreshness(); };
        state.DataChanged += UpdateFreshness;
        state.NavigationRequested += OnNavigationRequested;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.SearchText)) OnPropertyChanged(nameof(SearchText));
        };

        SelectedNav = NavItems[0];
        UpdateFreshness();
    }

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (value is not null) CurrentPage = _pages[value.Key];
    }

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        _loc.SetLanguage(value.Code);
        State.SaveSetting(AppState.SettingLanguage, value.Code);
    }

    /// <summary>사이드바 항목 클릭: 이미 선택된 항목이어도 상세 페이지에서 목록 페이지로 돌아온다.</summary>
    public void ShowNav(NavItemViewModel item)
    {
        SelectedNav = item;
        CurrentPage = _pages[item.Key];
    }

    private void OnNavigationRequested(PageKey page, string? arg)
    {
        // 상세 페이지는 사이드바에 없으므로 부모 항목을 강조하고 본문만 바꾼다
        var navKey = page switch { PageKey.Category => PageKey.Dashboard, PageKey.Compare => PageKey.Leaderboard, PageKey.Model => PageKey.Leaderboard, _ => page };
        SelectedNav = NavItems.First(n => n.Key == navKey);
        switch (page)
        {
            case PageKey.Category when arg is not null:
                _categoryDetail.Load(arg);
                CurrentPage = _categoryDetail;
                break;
            case PageKey.Model when long.TryParse(arg, out var modelId):
                _modelDetail.Load(modelId);
                CurrentPage = _modelDetail;
                break;
            case PageKey.Compare when arg is not null:
                _compare.Load(arg);
                CurrentPage = _compare;
                break;
            case PageKey.Metrics when arg is not null:
                Metrics.Select(arg);
                break;
            case PageKey.Leaderboard:
                Leaderboard.PendingSortCategoryId = arg;
                break;
        }
    }

    private void RefreshTitles()
    {
        foreach (var n in NavItems) n.Title = _loc[n.TitleKey];
    }

    private void UpdateFreshness()
    {
        if (!State.HasData)
        {
            Freshness = _loc["Fresh_NoData"];
            FreshnessWarning = true;
        }
        else if (State.IsStale)
        {
            Freshness = _loc.Format("Fresh_Stale", Fmt.Date(State.LastRefreshAt));
            FreshnessWarning = true;
        }
        else
        {
            Freshness = _loc.Format("Fresh_Updated", Fmt.Date(State.LastRefreshAt));
            FreshnessWarning = false;
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        SetTheme(dark ? "light" : "dark");
    }

    private void ApplySavedTheme() => SetTheme(State.ReadSetting(AppState.SettingTheme) ?? "dark", persist: false);

    private void SetTheme(string theme, bool persist = true)
    {
        switch (theme)
        {
            case "light": ApplicationThemeManager.Apply(ApplicationTheme.Light); break;
            case "system": ApplicationThemeManager.ApplySystemTheme(); break;
            default: ApplicationThemeManager.Apply(ApplicationTheme.Dark); break;
        }

        ThemeGlyph = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? "\uE706" : "\uE708"; // 해 / 달
        if (persist) State.SaveSetting(AppState.SettingTheme, theme);
        LLMPulse.App.Charts.ChartTheme.RaiseChanged();
    }
}

public sealed partial class AboutViewModel : ObservableObject
{
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Loc _loc = Loc.Instance;
    public Version CurrentVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    public string VersionText => _loc.Format("About_Version", CurrentVersion.ToString(4));
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private string? _updateUrl;
    [ObservableProperty] private bool _checking;
    /// <summary>설치판에서 새 버전이 있을 때 "지금 설치하고 다시 시작" 버튼을 보인다.</summary>
    [ObservableProperty] private bool _canInstall;
    public IAsyncRelayCommand CheckUpdateCommand { get; }
    public IAsyncRelayCommand InstallUpdateCommand { get; }

    public AboutViewModel()
    {
        CheckUpdateCommand = new AsyncRelayCommand(CheckAsync);
        InstallUpdateCommand = new AsyncRelayCommand(async () =>
        {
            UpdateText = _loc["About_Installing"];
            try { await LLMPulse.App.Services.AppUpdater.ApplyNowAsync(); }
            catch (Exception ex) { UpdateText = _loc.Format("Settings_ErrorDetail", ex.Message); }
        });
    }

    private async Task CheckAsync()
    {
        Checking = true;
        UpdateUrl = null;
        CanInstall = false;
        UpdateText = _loc["About_Checking"];
        try
        {
            // 설치판: Velopack 피드(GitHub Releases)로 확인하고 바로 설치할 수 있다
            if (LLMPulse.App.Services.AppUpdater.IsInstalled)
            {
                var v = await LLMPulse.App.Services.AppUpdater.CheckAsync();
                UpdateText = v is null ? _loc["About_UpToDate"] : _loc.Format("About_UpdateAvailable", v);
                CanInstall = v is not null;
                return;
            }

            var info = await new LLMPulse.Core.UpdateChecker(Http).CheckAsync(CurrentVersion);
            if (info is null) UpdateText = _loc["About_NoRelease"];
            else if (info.IsNewer) { UpdateText = _loc.Format("About_UpdateAvailable", info.Tag); UpdateUrl = info.Url; }
            else UpdateText = _loc["About_UpToDate"];
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            UpdateText = _loc["SrcErr_Network"];
        }
        catch (Exception ex)
        {
            // Velopack 피드 오류 (비공개 저장소 404 등)
            UpdateText = _loc.Format("Settings_ErrorDetail", ex.Message);
        }
        finally { Checking = false; }
    }
}
