using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;
using LLMPulse.App.Services;
using LLMPulse.Core.Scheduling;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;

namespace LLMPulse.App.ViewModels;

public sealed class SourceStatusViewModel
{
    public required string Source { get; init; }
    public required string LastSuccess { get; init; }
    public required string Status { get; init; }
    public required string Message { get; init; }
    public string DataAsOf { get; init; } = "";
    public string StaleWarning { get; init; } = "";
    public bool IsStale => StaleWarning.Length > 0;
    public bool IsError { get; init; }
    public string Hint { get; init; } = "";
    public bool HasHint => Hint.Length > 0;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IApiKeyStore _keys;
    private readonly TaskSchedulerService _scheduler;
    private readonly Loc _loc = Loc.Instance;
    private bool? _registered;
    private string _scheduleMessage = "";

    public RefreshCoordinator Refresher { get; }
    public ObservableCollection<SourceStatusViewModel> Sources { get; } = [];

    private string _keyStatusKey = "";
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private string _toleranceText = "";
    [ObservableProperty] private string _refreshTimeText = "";
    [ObservableProperty] private string _scheduleStatus = "";

    public bool NotifyEnabled
    {
        get => _state.GetFlag(AppState.SettingNotify, true);
        set { _state.SetFlag(AppState.SettingNotify, value); OnPropertyChanged(); }
    }

    public bool AutoClaudeUsage
    {
        get => _state.GetFlag(AppState.SettingAutoClaudeUsage, false);
        set { _state.SetFlag(AppState.SettingAutoClaudeUsage, value); OnPropertyChanged(); if (value) _ = _state.SyncLocalUsageAsync(); }
    }

    public bool ClaudeHistoryFound => _state.ClaudeCodeHistoryExists;

    /// <summary>설치판 자동 업데이트 (기본 켬). 끄면 정보 화면의 버튼으로만 확인한다.</summary>
    public bool AutoUpdate
    {
        get => _state.GetFlag(AppUpdater.SettingAuto, true);
        set { _state.SetFlag(AppUpdater.SettingAuto, value); OnPropertyChanged(); }
    }

    /// <summary>오래된(90일 넘게 갱신 없는) 소스도 추천 계산에 넣는다. 기본은 끔.</summary>
    public bool IncludeStaleSources
    {
        get => _state.GetFlag(LLMPulse.Core.SourceFreshness.SettingIncludeStale, false);
        set { _state.SetFlag(LLMPulse.Core.SourceFreshness.SettingIncludeStale, value); OnPropertyChanged(); _state.Reload(); }
    }

    public bool NotifyTop
    {
        get => _state.GetFlag(AppState.SettingNotifyTop, true);
        set { _state.SetFlag(AppState.SettingNotifyTop, value); OnPropertyChanged(); }
    }

    public bool NotifyPrice
    {
        get => _state.GetFlag(AppState.SettingNotifyPrice, true);
        set { _state.SetFlag(AppState.SettingNotifyPrice, value); OnPropertyChanged(); }
    }

    public bool NotifyLimit
    {
        get => _state.GetFlag(AppState.SettingNotifyLimit, true);
        set { _state.SetFlag(AppState.SettingNotifyLimit, value); OnPropertyChanged(); }
    }

    public bool TrayResident
    {
        get => _state.GetFlag(AppState.SettingTrayResident, false);
        set { _state.SetFlag(AppState.SettingTrayResident, value); OnPropertyChanged(); }
    }

    /// <summary>Windows 시작 시 트레이로 자동 실행 (켜면 트레이 상주도 함께 켠다).</summary>
    public bool StartWithWindows
    {
        get => Services.StartupService.IsEnabled();
        set
        {
            try { Services.StartupService.Set(value); }
            catch (Exception) { OnPropertyChanged(); return; }
            if (value) TrayResident = true;
            OnPropertyChanged();
        }
    }

    public IRelayCommand RegisterCommand { get; }
    public IRelayCommand UnregisterCommand { get; }

    /// <summary>슬라이더용 50–100 (%) 값.</summary>
    public double TolerancePercent
    {
        get => Math.Round(_state.Tolerance * 100);
        set
        {
            _state.Tolerance = Math.Clamp(value / 100.0, 0.5, 1.0);
            OnPropertyChanged();
            UpdateToleranceText();
        }
    }

    private readonly Database _db;
    [ObservableProperty] private string _backupStatus = "";

    public SettingsViewModel(AppState state, IApiKeyStore keys, RefreshCoordinator refresher, TaskSchedulerService scheduler, Database db)
    {
        _db = db;
        _state = state;
        _keys = keys;
        _scheduler = scheduler;
        Refresher = refresher;
        _refreshTimeText = state.RefreshTime.ToString("HH:mm");
        RegisterCommand = new RelayCommand(() => _ = ApplyScheduleAsync(register: true));
        UnregisterCommand = new RelayCommand(() => _ = ApplyScheduleAsync(register: false));
        _ = QueryScheduleAsync();
        state.DataChanged += Rebuild;
        _loc.LanguageChanged += Rebuild;
        Rebuild();
    }

    /// <summary>DB 백업 파일 내보내기 (API 키는 DB 밖 DPAPI 저장소라 포함되지 않음).</summary>
    [RelayCommand]
    private void ExportBackup()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"llmpulse-backup-{DateTime.Now:yyyyMMdd}.db",
            Filter = "LLM Pulse backup (*.db)|*.db",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _db.BackupTo(dlg.FileName);
            BackupStatus = _loc.Format("Backup_Exported", dlg.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            BackupStatus = _loc.Format("Settings_ErrorDetail", ex.Message);
        }
    }

    /// <summary>백업 파일로 바꾸기. 검사에 실패하면 현재 데이터는 그대로 둔다.</summary>
    [RelayCommand]
    private void ImportBackup()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "LLM Pulse backup (*.db)|*.db|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        if (System.Windows.MessageBox.Show(_loc["Backup_ConfirmImport"], "LLM Pulse",
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.OK) return;
        try
        {
            var error = _db.RestoreFrom(dlg.FileName);
            BackupStatus = error is null ? _loc["Backup_Imported"] : _loc.Format("Backup_Rejected", error);
            if (error is null) _state.Reload();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            BackupStatus = _loc.Format("Settings_ErrorDetail", ex.Message);
        }
    }

    partial void OnRefreshTimeTextChanged(string value)
    {
        if (!RefreshSchedule.TryParseTime(value, out var time))
        {
            _scheduleMessage = "Settings_TimeInvalid";
            UpdateScheduleStatus();
            return;
        }

        _scheduleMessage = "";
        _state.SaveSetting(AppState.SettingRefreshTime, time.ToString("HH:mm"));
        // 이미 등록돼 있으면 새 시각으로 다시 등록
        if (_registered == true) _ = ApplyScheduleAsync(register: true);
        else UpdateScheduleStatus();
    }

    private async Task QueryScheduleAsync()
    {
        _registered = await Task.Run(_scheduler.IsRegistered);
        UpdateScheduleStatus();
    }

    private async Task ApplyScheduleAsync(bool register)
    {
        var time = _state.RefreshTime;
        var (ok, msg) = await Task.Run(() => register ? _scheduler.Register(time) : _scheduler.Unregister());
        if (ok) { _registered = register; _scheduleMessage = ""; }
        else _scheduleMessage = "!" + msg;
        UpdateScheduleStatus();
    }

    private void UpdateScheduleStatus()
    {
        ScheduleStatus = _scheduleMessage switch
        {
            "Settings_TimeInvalid" => _loc["Settings_TimeInvalid"],
            { Length: > 1 } m when m[0] == '!' => _loc.Format("Settings_ScheduleFailed", m[1..]),
            _ => _registered switch
            {
                true => _loc.Format("Settings_ScheduleRegistered", _state.RefreshTime.ToString("HH:mm")),
                false => _loc["Settings_ScheduleNotRegistered"],
                null => "",
            },
        };
    }

    public void SaveApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            _keyStatusKey = "Settings_KeyEmpty";
            KeyStatus = _loc[_keyStatusKey];
            return;
        }

        _keys.Set(ArtificialAnalysisAdapter.Source, key);
        _keyStatusKey = "Settings_KeySaved";
        KeyStatus = _loc[_keyStatusKey];
    }

    private void Rebuild()
    {
        UpdateToleranceText();
        UpdateScheduleStatus();
        if (_keyStatusKey is "" or "Settings_KeyStored" or "Settings_KeyMissing")
            _keyStatusKey = _keys.Has(ArtificialAnalysisAdapter.Source) ? "Settings_KeyStored" : "Settings_KeyMissing";
        KeyStatus = _loc[_keyStatusKey];

        Sources.Clear();
        var fresh = _state.Freshness.ToDictionary(f => f.SourceId);
        foreach (var s in _state.SourceStatuses)
        {
            var f = fresh.GetValueOrDefault(s.Source);
            Sources.Add(new SourceStatusViewModel
            {
                Source = AppState.SourceLabel(s.Source),
                DataAsOf = f?.AsOf is { } asOf
                    ? _loc.Format(f.FromSource ? "Settings_DataAsOfSource" : "Settings_DataAsOfFetch",
                        asOf.ToString("yyyy-MM-dd"), f.AgeDays?.ToString("N0", _loc.Culture) ?? Fmt.Dash)
                    : Fmt.Dash,
                StaleWarning = f is { IsStale: true }
                    ? _loc.Format(IncludeStaleSources ? "Settings_StaleIncluded" : "Settings_StaleExcluded", LLMPulse.Core.SourceFreshness.StaleDays)
                    : "",
                LastSuccess = s.LastSuccessAt is not null && DateTimeOffset.TryParse(s.LastSuccessAt, out var t)
                    ? Fmt.Date(t) : Fmt.Dash,
                Status = s.LastStatus switch { "ok" => _loc["Settings_StatusOk"], "error" => _loc["Settings_StatusError"], null => Fmt.Dash, var o => o },
                Message = LocalizeMessage(s.LastStatus, s.LastMessage),
                IsError = s.LastStatus == "error",
                Hint = SourceErrors.Classify(s.LastStatus, s.LastMessage) switch
                {
                    SourceErrorKind.KeyMissing => _loc["SrcErr_KeyMissing"],
                    SourceErrorKind.KeyInvalid => _loc["SrcErr_KeyInvalid"],
                    SourceErrorKind.RateLimited => _loc["SrcErr_RateLimited"],
                    SourceErrorKind.Network => _loc["SrcErr_Network"],
                    SourceErrorKind.Other => _loc["SrcErr_Other"],
                    _ => "",
                },
            });
        }
    }

    /// <summary>refresh_log 의 영문 원문을 화면 언어로: 성공은 "모델 N개", 실패는 "상세: 원문".</summary>
    private string LocalizeMessage(string? status, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        if (status == "ok" && System.Text.RegularExpressions.Regex.Match(message, @"^(\d+) models( \(cached\))?$") is { Success: true } m)
            return _loc.Format("Settings_SourceModels", int.Parse(m.Groups[1].Value).ToString("N0", _loc.Culture))
                + (m.Groups[2].Success ? _loc["Settings_SourceCached"] : "");
        return status == "error" ? _loc.Format("Settings_ErrorDetail", message) : message;
    }

    private void UpdateToleranceText() =>
        ToleranceText = _loc.Format("Settings_ToleranceValue", Fmt.Percent(_state.Tolerance, "0%"));
}
