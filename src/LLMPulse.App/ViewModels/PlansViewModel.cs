using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;
using LLMPulse.Core.Plans;
using LLMPulse.Core.Scoring;

namespace LLMPulse.App.ViewModels;

/// <summary>구독 한 개의 모델 한 줄: 접근 수준(포함/한도/불가) 편집.</summary>
public sealed partial class PlanModelRowViewModel : ObservableObject
{
    private readonly PlanCardViewModel _card;
    public long ModelId { get; }
    public string Name { get; }
    public string Vendor { get; }

    private int _accessIndex;
    [ObservableProperty] private bool _isOverridden;

    /// <summary>0=불가, 1=한도, 2=포함. 콤보박스가 목록을 다시 채우며 보내는 -1(선택 해제)은 무시하고 현재 값을 되돌려 보낸다.</summary>
    public int AccessIndex
    {
        get => _accessIndex;
        set
        {
            if (value < 0) { OnPropertyChanged(); return; }
            if (!SetProperty(ref _accessIndex, value)) return;
            if (!_syncing) _card.SetAccess(this, (PlanAccess)Math.Clamp(value, 0, 2));
        }
    }

    public IRelayCommand ResetCommand { get; }

    public PlanModelRowViewModel(PlanCardViewModel card, ModelInput m, PlanAccess access, bool overridden)
    {
        _card = card;
        ModelId = m.Id;
        Name = m.Name;
        Vendor = m.Vendor ?? "";
        _accessIndex = (int)access;
        _isOverridden = overridden;
        ResetCommand = new RelayCommand(() => card.SetAccess(this, null), () => IsOverridden);
    }

    /// <summary>상태 갱신 시 사용자 편집 이벤트를 일으키지 않고 값을 맞춘다.</summary>
    public void Sync(PlanAccess access, bool overridden)
    {
        _syncing = true;
        AccessIndex = (int)access;
        IsOverridden = overridden;
        _syncing = false;
        (ResetCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private bool _syncing;

}

public sealed partial class PlanCardViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly Loc _loc = Loc.Instance;
    private readonly PlanDef _def;
    private bool _loadingLimit;
    private List<PlanModelRowViewModel> _allRows = [];

    public string Id => _def.Id;
    public PlanDef Def => _def;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _breakEven = "";

    // 한도 메모
    [ObservableProperty] private string _capText = "";
    private int _periodIndex = 3;

    /// <summary>한도 기간 콤보박스 (목록 재구성 시 오는 -1 은 무시하고 값을 되돌려 보낸다).</summary>
    public int PeriodIndex
    {
        get => _periodIndex;
        set
        {
            if (value < 0) { OnPropertyChanged(); return; }
            if (SetProperty(ref _periodIndex, value) && !_loadingLimit) SaveLimit();
        }
    }
    [ObservableProperty] private string _limitNote = "";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private double _usageRatio;
    [ObservableProperty] private bool _hasCap;
    [ObservableProperty] private bool _nearLimit;

    // 모델 편집
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _modelFilter = "";
    [ObservableProperty] private string _modelSummary = "";

    public ObservableCollection<PlanModelRowViewModel> Rows { get; } = [];

    public IRelayCommand PlusCommand { get; }
    public IRelayCommand MinusCommand { get; }
    public IRelayCommand ResetUsageCommand { get; }

    public bool IsSelected
    {
        get => _state.IsPlanSelected(Id);
        set { _state.SetPlanSelected(Id, value); OnPropertyChanged(); }
    }

    public PlanCardViewModel(AppState state, PlanDef def)
    {
        _state = state;
        _def = def;
        PlusCommand = new RelayCommand(() => Bump(+1));
        MinusCommand = new RelayCommand(() => Bump(-1));
        ResetUsageCommand = new RelayCommand(() => { EnsureLimitRow(); _state.ResetUsage(Id); LoadLimit(); });
        LoadLimit();
        state.LimitsChanged += LoadLimit;
    }

    public void Refresh(IReadOnlyList<ModelInput> models)
    {
        var lang = _loc.Language;
        var price = _def.MonthlyUsd is { } usd
            ? (usd == 0 ? _loc["Plan_Free"] : _loc.Format("Plan_PerMonth", Fmt.Number(usd, "0.##")))
            : _loc["Plan_PerToken"];
        var count = models.Count(m => _state.AccessIn(_def, m) != PlanAccess.None);
        Title = _def.Name.Get(lang);
        Detail = _loc.Format("Plan_Detail", price, count.ToString("N0", _loc.Culture), _def.Reviewed);
        Note = _def.Note.Get(lang);
        BreakEven = BuildBreakEven();
        OnPropertyChanged(nameof(IsSelected));
        LoadLimit();
        if (IsExpanded) RebuildRows(models);
    }

    /// <summary>정액 요금이 API 종량제로는 몇 토큰어치인지 (이 구독 안에서 가장 좋은 모델 기준).</summary>
    private string BuildBreakEven()
    {
        if (_def.MonthlyUsd is not > 0 || _state.Snapshot is not { } snap) return "";
        var cr = snap.Categories.FirstOrDefault(c => c.Category.Id == DashboardViewModel.PickCategoryId);
        if (cr is null) return "";

        var best = Recommender.TopN(cr.Scores, snap.Models, 1,
            id => snap.Models[id] is { BlendedPrice: > 0 } m && _state.AccessIn(_def, m) != PlanAccess.None).FirstOrDefault();
        if (best is null) return "";

        var model = snap.Models[best.ModelId];
        return PlanEconomics.BreakEvenMillionTokens(_def.MonthlyUsd.Value, model.BlendedPrice!.Value) is { } tokens
            ? _loc.Format("Plans_BreakEven", Fmt.Number(_def.MonthlyUsd.Value, "0.##"), Fmt.Number(tokens, "0.#"),
                model.Name, Fmt.Price(model.BlendedPrice))
            : "";
    }

    // ---- 모델 접근 편집 ----

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) RebuildRows(_state.Snapshot?.Models.Values.ToList() ?? []);
    }

    partial void OnModelFilterChanged(string value) => ApplyRowFilter();

    private void RebuildRows(IReadOnlyList<ModelInput> models)
    {
        var ordered = models.OrderBy(m => m.Vendor ?? "").ThenBy(m => m.Name).ToList();
        var ids = ordered.Select(m => m.Id).ToList();

        if (_allRows.Count == ids.Count && _allRows.Select(r => r.ModelId).SequenceEqual(ids))
        {
            // 같은 목록이면 행을 유지하고 값만 갱신해, 편집 중인 콤보박스가 사라지지 않게 한다
            foreach (var (row, m) in _allRows.Zip(ordered))
                row.Sync(_state.AccessIn(_def, m), _state.HasOverride(Id, m.Name));
        }
        else
        {
            _allRows = ordered.Select(m =>
                new PlanModelRowViewModel(this, m, _state.AccessIn(_def, m), _state.HasOverride(Id, m.Name))).ToList();
            ApplyRowFilter();
        }

        var full = _allRows.Count(r => r.AccessIndex == 2);
        var limited = _allRows.Count(r => r.AccessIndex == 1);
        ModelSummary = _loc.Format("Plans_ModelSummary", full.ToString("N0", _loc.Culture),
            limited.ToString("N0", _loc.Culture), _allRows.Count(r => r.IsOverridden).ToString("N0", _loc.Culture));
    }

    private void ApplyRowFilter()
    {
        var q = ModelFilter.Trim();
        Rows.Clear();
        foreach (var r in _allRows)
            if (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || r.Vendor.Contains(q, StringComparison.OrdinalIgnoreCase))
                Rows.Add(r);
    }

    public void SetAccess(PlanModelRowViewModel row, PlanAccess? access)
    {
        // 사용자가 기본 규칙과 같은 값을 고르면 보정을 지운다 (불필요한 override 방지)
        if (access is { } a && _state.Snapshot?.Models.TryGetValue(row.ModelId, out var m) == true)
        {
            var baseline = PlanCatalog.Matches(_def, m) ? PlanAccess.Full : PlanAccess.None;
            if (a == baseline) access = null;
        }

        _state.SetOverride(Id, row.Name, access);
    }

    // ---- 한도 메모 / 사용량 ----

    private void EnsureLimitRow()
    {
        if (_state.GetLimit(Id) is null) SaveLimit();
    }

    private void SaveLimit()
    {
        if (_loadingLimit) return;
        int? cap = int.TryParse(CapText.Trim(), out var c) && c > 0 ? c : null;
        _state.SaveLimit(Id, cap, PlanLimit.Periods[Math.Clamp(PeriodIndex, 0, PlanLimit.Periods.Length - 1)], LimitNote.Trim());
        LoadLimit();
    }

    partial void OnCapTextChanged(string value) { if (!_loadingLimit) SaveLimit(); }
    partial void OnLimitNoteChanged(string value) { if (!_loadingLimit) SaveLimit(); }

    private void Bump(int delta)
    {
        EnsureLimitRow();
        _state.AddUsage(Id, delta);
        LoadLimit();
    }

    public void LoadLimit()
    {
        _loadingLimit = true;
        try
        {
            var l = _state.GetLimit(Id);
            CapText = l?.Cap?.ToString() ?? "";
            PeriodIndex = l is null ? 3 : Math.Max(0, Array.IndexOf(PlanLimit.Periods, l.Period));
            LimitNote = l?.Note ?? "";
            HasCap = l?.Cap is > 0;
            UsageRatio = l?.Ratio ?? 0;
            NearLimit = l?.Ratio is >= 0.8;
            UsageText = l is null
                ? _loc["Plans_NoUsage"]
                : l.Cap is { } cap
                    ? _loc.Format("Plans_UsageCap", l.Used.ToString("N0", _loc.Culture), cap.ToString("N0", _loc.Culture))
                    : _loc.Format("Plans_UsageNoCap", l.Used.ToString("N0", _loc.Culture));
            if (l is { Cap: > 0 } && TimeText.ResetsIn(l.ResetAt(DateTimeOffset.UtcNow), DateTimeOffset.UtcNow) is { Length: > 0 } rs)
                UsageText += " · " + rs;
        }
        finally { _loadingLimit = false; }
    }
}

public sealed partial class PlansViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly Loc _loc = Loc.Instance;
    private bool _syncingProfile;

    public ObservableCollection<PlanCardViewModel> Cards { get; } = [];
    public ObservableCollection<string> Profiles { get; } = [];
    public IReadOnlyList<string> AccessOptions =>
        [_loc["Plans_AccessNone"], _loc["Plans_AccessLimited"], _loc["Plans_AccessFull"]];
    public IReadOnlyList<string> PeriodOptions =>
        [_loc["Plans_Period5h"], _loc["Plans_PeriodDay"], _loc["Plans_PeriodWeek"], _loc["Plans_PeriodMonth"]];

    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private string _economics = "";
    [ObservableProperty] private bool _hasEconomics;
    [ObservableProperty] private string _monthlyTokensText = "30";
    [ObservableProperty] private string _pathText = "";

    public IRelayCommand SaveProfileCommand { get; }
    public IRelayCommand DeleteProfileCommand { get; }
    public IRelayCommand RunWizardCommand { get; }

    public IRelayCommand OpenDataFolderCommand { get; } = new RelayCommand(() =>
    {
        System.IO.Directory.CreateDirectory(LLMPulse.Core.Storage.AppPaths.DataDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LLMPulse.Core.Storage.AppPaths.DataDirectory)
        { UseShellExecute = true });
    });

    public PlansViewModel(AppState state, WizardViewModel wizard)
    {
        _state = state;
        _loadingTokens = true;
        _monthlyTokensText = double.TryParse(state.ReadSetting(AppState.SettingMonthlyTokens), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var savedTokens)
            ? savedTokens.ToString("0.###", Localization.Loc.Instance.Culture) : "30"; // 저장은 Invariant, 화면은 현재 언어 표기
        _loadingTokens = false;
        SaveProfileCommand = new RelayCommand(() =>
        {
            var name = NewProfileName.Trim();
            if (name.Length == 0) return;
            _state.SaveProfile(name);
            NewProfileName = "";
            Rebuild();
        });
        DeleteProfileCommand = new RelayCommand(() =>
        {
            if (SelectedProfile is { } p) { _state.DeleteProfile(p); Rebuild(); }
        }, () => SelectedProfile is not null);
        RunWizardCommand = new RelayCommand(wizard.Open);

        state.DataChanged += Rebuild;
        _loc.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(AccessOptions));
            OnPropertyChanged(nameof(PeriodOptions));
            Rebuild();
            // 콤보박스가 새 목록에서 선택을 잃었을 수 있으니 각 값을 다시 밀어 넣는다
            foreach (var c in Cards) { c.LoadLimit(); foreach (var r in c.Rows) r.Sync((PlanAccess)r.AccessIndex, r.IsOverridden); }
        };
        Rebuild();
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        (DeleteProfileCommand as RelayCommand)?.NotifyCanExecuteChanged();
        if (!_syncingProfile && value is not null && !string.Equals(value, _state.ActiveProfile, StringComparison.OrdinalIgnoreCase))
            _state.SwitchProfile(value);
    }

    partial void OnMonthlyTokensTextChanged(string value)
    {
        if (_loadingTokens) return;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v) && v > 0)
            _state.SaveSetting(AppState.SettingMonthlyTokens, v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        UpdateEconomics();
    }

    private bool _loadingTokens;

    /// <summary>선택한 구독 합계 vs 같은 사용량을 API 로 쓸 때의 추정 비용.</summary>
    private void UpdateEconomics()
    {
        HasEconomics = false;
        Economics = "";
        var snap = _state.Snapshot;
        var total = _state.ChosenPlans.Where(p => p.MonthlyUsd is > 0).Sum(p => p.MonthlyUsd!.Value);
        if (snap is null || total <= 0) return;
        if (!double.TryParse(MonthlyTokensText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var usage) || usage <= 0) return;

        var cr = snap.Categories.FirstOrDefault(c => c.Category.Id == DashboardViewModel.PickCategoryId);
        var best = cr is null ? null : Recommender.TopN(cr.Scores, snap.Models, 1,
            id => snap.Models[id] is { BlendedPrice: > 0 } m && _state.AccessOf(m) != PlanAccess.None).FirstOrDefault();
        if (best is null) return;

        var model = snap.Models[best.ModelId];
        var api = PlanEconomics.ApiCost(usage, model.BlendedPrice!.Value);
        var line = _loc.Format("Plans_Econ", Fmt.Number(total, "0.##"), Fmt.Number(usage, "0.#"), model.Name, Fmt.Number(api, "0.##"));
        var diff = Math.Abs(api - total);
        var verdict = api > total
            ? _loc.Format("Plans_EconSubCheaper", Fmt.Number(diff, "0.##"))
            : _loc.Format("Plans_EconApiCheaper", Fmt.Number(diff, "0.##"));
        Economics = $"{line} {verdict}";
        HasEconomics = true;
    }

    private void Rebuild()
    {
        var plans = _state.Plans;
        if (Cards.Count != plans.Plans.Count || !Cards.Select(c => c.Id).SequenceEqual(plans.Plans.Select(p => p.Id)))
        {
            Cards.Clear();
            foreach (var p in plans.Plans) Cards.Add(new PlanCardViewModel(_state, p));
        }

        var models = _state.Snapshot?.Models.Values.ToList() ?? [];
        foreach (var c in Cards) c.Refresh(models);

        _syncingProfile = true;
        Profiles.Clear();
        foreach (var n in _state.ProfileNames) Profiles.Add(n);
        SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p, _state.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        _syncingProfile = false;

        UpdateEconomics();
        Warning = plans.LoadWarning is { } w ? _loc.Format("Plan_LoadWarning", w) : "";
        // 원격 목록: 쓰는 중인지 + 마지막 시도 결과 (실패하면 내장본 또는 이전 사본을 계속 쓴다)
        var remote = _state.GetSetting(PlanCatalogUpdater.SettingStatus) is { } st && st.Split('|', 2) is [var kind, var detail]
            ? kind == "ok" && DateTimeOffset.TryParse(detail, out var at)
                ? _loc.Format("Plan_RemoteOk", Fmt.Date(at))
                : _loc.Format("Plan_RemoteFail", detail)
            : "";
        PathText = (plans.IsUserDefined
            ? _loc.Format("Plan_UserFile", PlanCatalog.UserFileName)
            : (plans.IsRemote ? _loc["Plan_RemoteInUse"] + " " : _loc["Plan_BuiltInUse"] + " ")
              + _loc.Format("Plan_UserFileHint", PlanCatalog.UserFileName))
            + (remote.Length > 0 ? " " + remote : "");
    }
}
