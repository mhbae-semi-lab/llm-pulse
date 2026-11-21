using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using LLMPulse.Core;
using LLMPulse.Core.Models;
using LLMPulse.Core.Plans;
using LLMPulse.Core.Scoring;
using LLMPulse.Core.Storage;

namespace LLMPulse.App;

public sealed record LimitInfo(PlanDef Plan, int Used, int Cap, double Ratio, DateTimeOffset? ResetAt);

public enum PageKey { Dashboard, Leaderboard, Metrics, Settings, About, Category, Compare, Matching, Plans, Model, History }

/// <summary>화면 간 공유 상태: 점수 스냅샷, 검색어, 허용치, 마지막 갱신, 내비게이션 요청, 내 구독.</summary>
public sealed partial class AppState : ObservableObject
{
    public const string SettingTolerance = "tolerance";
    public const string SettingLanguage = "language";
    public const string SettingTheme = "theme";
    public const string SettingPlans = "my_plans";
    public const string SettingPlanOnly = "plan_only";
    public const string SettingProfiles = "plan_profiles";
    public const string SettingActiveProfile = "plan_profile_active";
    public const string SettingOnboarded = "onboarded";
    public const string SettingWeights = "metric_weights";
    public const string SettingWeightPresets = "weight_presets";
    public const string SettingFavorites = "favorites";
    public const string SettingHiddenColumns = "lb_hidden_columns";
    public const string SettingMonthlyTokens = "monthly_tokens_m";
    public const string SettingRefreshTime = "refresh_time";
    public const string SettingNotify = "notify";
    public const string SettingTrayResident = "tray_resident";
    public const string SettingLastNotified = "last_notified_date";
    public const string SettingAutoClaudeUsage = "auto_claude_usage";
    public const string SettingNotifyTop = "notify_top";
    public const string SettingNotifyPrice = "notify_price";
    public const string SettingNotifyLimit = "notify_limit";
    public const string SettingLastNotifiedPrice = "last_notified_price_date";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);
    public static readonly TimeSpan ManualRefreshMinInterval = TimeSpan.FromMinutes(10);

    private readonly ScoringService _scoring;
    private readonly SqliteStore _store;
    private readonly LLMPulse.Core.Catalog _catalog;
    private Dictionary<string, Dictionary<string, double>> _weights = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedPlans = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<string, PlanAccess>> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private List<PlanDef> _chosen = [];

    [ObservableProperty] private ScoringSnapshot? _snapshot;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private double _tolerance = Recommender.DefaultTolerance;
    [ObservableProperty] private DateTimeOffset? _lastRefreshAt;
    /// <summary>true 면 대시보드/순위/차트가 내 구독 안의 모델만 보여준다 (구독을 고른 경우에만 유효).</summary>
    [ObservableProperty] private bool _planOnly;
    [ObservableProperty] private string _activeProfile = "";

    public bool HasData => Snapshot is { Models.Count: > 0 };
    public bool IsStale => LastRefreshAt is { } t && DateTimeOffset.UtcNow - t > StaleAfter;

    /// <summary>데이터(스냅샷)가 새로 계산됐다.</summary>
    public event Action? DataChanged;

    /// <summary>다른 페이지로 이동 요청 (argument: 지표 id / 분야 id).</summary>
    public event Action<PageKey, string?>? NavigationRequested;

    public PlanCatalog Plans { get; }

    /// <summary>선택한 구독/접근 수단이 하나라도 있는가 (My Plan 표시 조건).</summary>
    public bool HasPlans => _chosen.Count > 0;

    /// <summary>실제로 제한을 적용 중인가 (구독이 있고 토글이 켜져 있음).</summary>
    public bool Restricting => PlanOnly && HasPlans;

    public IReadOnlyList<PlanDef> ChosenPlans => _chosen;

    public bool IsPlanSelected(string id) => _selectedPlans.Contains(id);

    public void SetPlanSelected(string id, bool selected)
    {
        if (!(selected ? _selectedPlans.Add(id) : _selectedPlans.Remove(id))) return;
        PersistSelection();
        Reload();
    }

    private void PersistSelection() =>
        _store.SetSetting(SettingPlans, string.Join(",", _selectedPlans.Order()));

    /// <summary>모델이 선택한 구독 중 하나에서 쓸 수 있는가. 구독 미선택이면 true.</summary>
    public bool InPlan(long modelId) =>
        !HasPlans || (Snapshot?.Models.TryGetValue(modelId, out var m) == true && AccessOf(m) != PlanAccess.None);

    /// <summary>선택한 구독들 중 가장 자유로운 접근 수준. 구독 미선택이면 Full.</summary>
    public PlanAccess AccessOf(ModelInput model) =>
        HasPlans ? PlanAccessResolver.Best(_chosen, model, _overrides) : PlanAccess.Full;

    public PlanAccess AccessOf(long modelId) =>
        Snapshot?.Models.TryGetValue(modelId, out var m) == true ? AccessOf(m) : PlanAccess.None;

    /// <summary>특정 플랜에서의 접근 수준 (보정 반영).</summary>
    public PlanAccess AccessIn(PlanDef plan, ModelInput model)
    {
        _overrides.TryGetValue(plan.Id, out var o);
        return PlanAccessResolver.Resolve(plan, model, o);
    }

    public bool HasOverride(string planId, string modelName) =>
        _overrides.TryGetValue(planId, out var o) && o.ContainsKey(modelName);

    /// <summary>access 가 null 이면 보정을 지우고 기본 규칙으로 되돌린다.</summary>
    public void SetOverride(string planId, string modelName, PlanAccess? access)
    {
        _store.SetPlanOverride(planId, modelName, access);
        _overrides = _store.LoadPlanOverrides();
        Reload();
    }

    // ---- 사용량 한도 ----
    public PlanLimit? GetLimit(string planId) => _store.GetPlanLimit(planId, DateTimeOffset.UtcNow);

    /// <summary>한도(상한)가 설정된 선택 구독들의 현재 사용 현황.</summary>
    public IReadOnlyList<LimitInfo> LimitInfos()
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<LimitInfo>();
        foreach (var plan in _chosen)
        {
            if (GetLimit(plan.Id) is not { Cap: > 0 } l) continue;
            list.Add(new LimitInfo(plan, l.Used, l.Cap!.Value, l.Ratio ?? 0, l.ResetAt(now)));
        }

        return list;
    }

    /// <summary>한도/사용량이 바뀌었을 때 (대시보드 경고 갱신용).</summary>
    public event Action? LimitsChanged;

    private readonly LLMPulse.Core.Usage.ClaudeCodeUsageReader _claudeUsage = new();

    public bool ClaudeCodeHistoryExists => _claudeUsage.Exists;

    /// <summary>
    /// 로컬 Claude Code 대화 기록으로 Claude 계열 구독(한도가 설정된 것)의 사용 횟수를 자동으로 맞춘다.
    /// 설정이 꺼져 있거나 기록 폴더가 없으면 아무것도 하지 않는다. UI 스레드에서 호출.
    /// </summary>
    public async Task SyncLocalUsageAsync()
    {
        if (!GetFlag(SettingAutoClaudeUsage, false) || !_claudeUsage.Exists) return;
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var plan in _chosen.Where(p => p.Id.StartsWith("claude", StringComparison.OrdinalIgnoreCase)))
        {
            if (_store.GetPlanLimit(plan.Id, now) is not { Cap: > 0 } l) continue;
            var since = LLMPulse.Core.Usage.ClaudeCodeUsageReader.WindowStart(l.Period, now);
            var prompts = await Task.Run(() => _claudeUsage.PromptsSince(since));
            DateTimeOffset? start = prompts.Count == 0 ? null : l.Period == "5h" ? prompts[0] : since;
            if (prompts.Count == l.Used && start == l.PeriodStart) continue;
            _store.SetPlanUsage(plan.Id, prompts.Count, start);
            changed = true;
        }

        if (changed) LimitsChanged?.Invoke();
    }

    /// <summary>시간 경과(초기화 카운트다운)만 바뀌었을 때 화면을 다시 그리게 한다.</summary>
    public void RaiseLimitsChanged() => LimitsChanged?.Invoke();

    public void SaveLimit(string planId, int? cap, string period, string note)
    {
        _store.SavePlanLimit(planId, cap, period, note);
        LimitsChanged?.Invoke();
    }

    public PlanLimit? AddUsage(string planId, int delta)
    {
        var l = _store.AddPlanUsage(planId, delta, DateTimeOffset.UtcNow);
        LimitsChanged?.Invoke();
        return l;
    }

    public void ResetUsage(string planId)
    {
        _store.ResetPlanUsage(planId);
        LimitsChanged?.Invoke();
    }

    // ---- 프로필 (예: 회사 / 개인) ----
    public IReadOnlyList<string> ProfileNames => _profiles.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    public void SaveProfile(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        _profiles[name] = _selectedPlans.Order().ToList();
        ActiveProfile = name;
        PersistProfiles();
        OnPropertyChanged(nameof(ProfileNames));
    }

    public void SwitchProfile(string name)
    {
        if (!_profiles.TryGetValue(name, out var ids)) return;
        _selectedPlans.Clear();
        foreach (var id in ids) _selectedPlans.Add(id);
        ActiveProfile = name;
        PersistSelection();
        _store.SetSetting(SettingActiveProfile, name);
        Reload();
    }

    public void DeleteProfile(string name)
    {
        if (!_profiles.Remove(name)) return;
        if (string.Equals(ActiveProfile, name, StringComparison.OrdinalIgnoreCase)) ActiveProfile = "";
        PersistProfiles();
        OnPropertyChanged(nameof(ProfileNames));
    }

    private void PersistProfiles()
    {
        _store.SetSetting(SettingProfiles, JsonSerializer.Serialize(_profiles));
        _store.SetSetting(SettingActiveProfile, ActiveProfile);
    }

    public AppState(ScoringService scoring, SqliteStore store, PlanCatalog plans, LLMPulse.Core.Catalog catalog)
    {
        _scoring = scoring;
        _store = store;
        _catalog = catalog;
        try
        {
            if (store.GetSetting(SettingWeights) is { Length: > 0 } wj)
                _weights = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(wj) ?? [];
        }
        catch (JsonException)
        {
            // 손상된 가중치 설정은 기본값으로 시작
        }

        Plans = plans;
        foreach (var id in (store.GetSetting(SettingPlans) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            _selectedPlans.Add(id);

        try
        {
            if (store.GetSetting(SettingProfiles) is { Length: > 0 } json)
                _profiles = new(JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? [],
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // 손상된 프로필 설정은 무시하고 빈 목록으로 시작
        }

        _activeProfile = store.GetSetting(SettingActiveProfile) ?? "";
        _planOnly = store.GetSetting(SettingPlanOnly) == "1";
        _overrides = store.LoadPlanOverrides();
        foreach (var f in (store.GetSetting(SettingFavorites) ?? "").Split(",", StringSplitOptions.RemoveEmptyEntries))
            if (long.TryParse(f, out var fid)) _favorites.Add(fid);

        if (double.TryParse(store.GetSetting(SettingTolerance),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var t))
            _tolerance = Math.Clamp(t, 0.5, 1.0);
    }

    /// <summary>첫 실행 마법사를 이미 마쳤는가.</summary>
    public bool Onboarded
    {
        get => GetFlag(SettingOnboarded, false);
        set => SetFlag(SettingOnboarded, value);
    }

    public IReadOnlyList<SourceStatus> SourceStatuses => _store.SourceStatuses();

    /// <summary>소스별 데이터 기준일 · 오래됨 여부 (오늘 UTC 기준).</summary>
    public IReadOnlyList<SourceFreshnessInfo> Freshness => _store.SourceFreshnessAll(DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>모델별 소스 간 불일치 (Reload 때 계산).</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<Disagreement>> Disagreements { get; private set; } =
        new Dictionary<long, IReadOnlyList<Disagreement>>();

    /// <summary>소스 id → 화면 이름. LMArena 는 2026-01 에 Arena 로 이름을 바꿨다 (id 는 기존 DB 호환을 위해 그대로).</summary>
    public static string SourceLabel(string id) => id switch
    {
        LLMPulse.Core.Sources.ArtificialAnalysisAdapter.Source => "Artificial Analysis",
        LLMPulse.Core.Sources.OpenRouterAdapter.Source => "OpenRouter",
        LLMPulse.Core.Sources.AiderAdapter.Source => "Aider",
        LLMPulse.Core.Sources.LmArenaAdapter.Source => "Arena",
        DemoData.Source => "Demo",
        _ => id,
    };

    public void Reload()
    {
        _chosen = Plans.Plans.Where(p => _selectedPlans.Contains(p.Id)).ToList();
        Snapshot = _scoring.Build(DateOnly.FromDateTime(DateTime.UtcNow), Tolerance,
            HasPlans ? AccessOf : null, Restricting, _weights);
        Disagreements = SourceDisagreement.Find(Snapshot.Models.Values.ToList(), _store.LoadPricePairs())
            .GroupBy(d => d.ModelId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Disagreement>)g.ToList());
        LoadChanges();
        OnPropertyChanged(nameof(HasPlans));
        OnPropertyChanged(nameof(Restricting));
        LastRefreshAt = ReadLastRefresh();
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(IsStale));
        DataChanged?.Invoke();
    }

    // ---- 지표 가중치 조정 ----

    public CategoryDef? OriginalCategory(string categoryId) => _catalog.Categories.FirstOrDefault(c => c.Id == categoryId);

    public bool HasCustomWeights(string categoryId) => _weights.ContainsKey(categoryId);

    /// <summary>현재 적용 중인 가중치 (조정값이 있으면 그것, 없으면 기본값).</summary>
    public IReadOnlyDictionary<string, double> CurrentWeights(string categoryId)
    {
        var cat = OriginalCategory(categoryId);
        if (cat is null) return new Dictionary<string, double>();
        _weights.TryGetValue(categoryId, out var custom);
        return cat.Metrics.ToDictionary(m => m.Id, m => custom is not null && custom.TryGetValue(m.Id, out var w) ? w : m.Weight);
    }

    public void SetWeights(string categoryId, IReadOnlyDictionary<string, double> edited)
    {
        var cat = OriginalCategory(categoryId);
        if (cat is null) return;
        var diff = WeightOverrides.Normalize(cat, edited);
        if (diff is null) _weights.Remove(categoryId);
        else _weights[categoryId] = diff;
        _store.SetSetting(SettingWeights, JsonSerializer.Serialize(_weights));
        Reload();
    }

    // ---- 이름 붙인 가중치 프리셋 (분야별) ----

    private Dictionary<string, Dictionary<string, Dictionary<string, double>>> _presets =
        new(StringComparer.Ordinal);

    public event Action? PresetsChanged;

    public IReadOnlyList<string> PresetNames(string categoryId) =>
        LoadPresets().TryGetValue(categoryId, out var d) ? d.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToList() : [];

    private Dictionary<string, Dictionary<string, Dictionary<string, double>>> LoadPresets()
    {
        try
        {
            if (_store.GetSetting(SettingWeightPresets) is { Length: > 0 } json)
                _presets = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, double>>>>(json) ?? [];
        }
        catch (JsonException) { _presets = []; }

        return _presets;
    }

    public void SavePreset(string categoryId, string name, IReadOnlyDictionary<string, double> weights)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var all = LoadPresets();
        if (!all.TryGetValue(categoryId, out var perCat)) all[categoryId] = perCat = new(StringComparer.CurrentCultureIgnoreCase);
        perCat[name] = new Dictionary<string, double>(weights);
        _store.SetSetting(SettingWeightPresets, JsonSerializer.Serialize(all));
        PresetsChanged?.Invoke();
    }

    public bool ApplyPreset(string categoryId, string name)
    {
        if (!LoadPresets().TryGetValue(categoryId, out var perCat) || !perCat.TryGetValue(name, out var w)) return false;
        SetWeights(categoryId, w);
        return true;
    }

    public void DeletePreset(string categoryId, string name)
    {
        var all = LoadPresets();
        if (!all.TryGetValue(categoryId, out var perCat) || !perCat.Remove(name)) return;
        _store.SetSetting(SettingWeightPresets, JsonSerializer.Serialize(all));
        PresetsChanged?.Invoke();
    }

    public void ResetWeights(string categoryId) => SetWeights(categoryId, new Dictionary<string, double>());

    // ---- 즐겨찾기 (핀) ----

    private readonly HashSet<long> _favorites = [];

    public bool IsFavorite(long modelId) => _favorites.Contains(modelId);
    public int FavoriteCount => _favorites.Count;
    public IReadOnlyCollection<long> Favorites => _favorites;

    /// <summary>즐겨찾기가 바뀌었다 (전체 재계산 없이 가벼운 화면만 갱신).</summary>
    public event Action? FavoritesChanged;

    public void SetFavorite(long modelId, bool value)
    {
        if (!(value ? _favorites.Add(modelId) : _favorites.Remove(modelId))) return;
        _store.SetSetting(SettingFavorites, string.Join(",", _favorites.Order()));
        FavoritesChanged?.Invoke();
    }

    // ---- 순위 추이 ----

    /// <summary>직전 저장 시점의 분야별 모델 순위 (비교용).</summary>
    public IReadOnlyDictionary<string, Dictionary<long, int>> PreviousRanks { get; private set; } =
        new Dictionary<string, Dictionary<long, int>>();

    /// <summary>모델의 분야 점수 최근 추이.</summary>
    public IReadOnlyList<string> ScoreDateList() => _store.ScoreDateList();

    public IReadOnlyDictionary<long, (double? Score, int? Rank)> CategoryScoresOn(string categoryId, string date) =>
        _store.LoadCategoryScoresOn(categoryId, date);

    public IReadOnlyDictionary<long, double> PricesAsOf(string date) => _store.LoadPricesAsOf(date);

    public IReadOnlyList<double> PriceHistory(long modelId) => _store.LoadPriceHistory(modelId);

    public IReadOnlyList<double> ScoreTrend(string categoryId, long modelId) =>
        _store.LoadScoreTrend(categoryId, modelId, 7);

    // ---- 변경 내역 ----

    /// <summary>직전 저장 시점 대비 변경 목록 (기본 가중치 기준, 구독 필터 전).</summary>
    public IReadOnlyList<ChangeItem> Changes { get; private set; } = [];
    public string? ChangesSince { get; private set; }

    /// <summary>가격이 5% 이상 바뀐 모델 (변동폭 큰 순).</summary>
    public IReadOnlyList<PriceChange> PriceChanges { get; private set; } = [];

    private void LoadChanges()
    {
        PriceChanges = _store.LoadPriceChanges();
        var (latest, prev) = _store.ScoreDates();
        if (latest is null || prev is null)
        {
            Changes = [];
            ChangesSince = null;
            PreviousRanks = new Dictionary<string, Dictionary<long, int>>();
            return;
        }

        var prevRanks = _store.LoadRanks(prev);
        PreviousRanks = prevRanks;
        Changes = ChangeFeed.Build(prevRanks, _store.LoadRanks(latest), _store.NewModelIds(latest));
        ChangesSince = prev;
    }

    public void SaveSetting(string key, string value) => _store.SetSetting(key, value);

    public string? GetSetting(string key) => _store.GetSetting(key);

    public bool GetFlag(string key, bool defaultValue) =>
        _store.GetSetting(key) is { } s ? s == "1" : defaultValue;

    public void SetFlag(string key, bool value) => _store.SetSetting(key, value ? "1" : "0");

    public TimeOnly RefreshTime =>
        LLMPulse.Core.Scheduling.RefreshSchedule.TryParseTime(_store.GetSetting(SettingRefreshTime), out var t)
            ? t : LLMPulse.Core.Scheduling.RefreshSchedule.DefaultTime;

    /// <summary>DB 에 기록된 마지막 갱신 시각 (헤드리스 작업이 갱신했을 수도 있으므로 캐시하지 않고 직접 읽는다).</summary>
    public DateTimeOffset? ReadLastRefresh() =>
        DateTimeOffset.TryParse(_store.GetSetting("last_refresh_at"),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null;
    public string? ReadSetting(string key) => _store.GetSetting(key);

    public void Navigate(PageKey page, string? argument = null) =>
        NavigationRequested?.Invoke(page, argument);

    partial void OnToleranceChanged(double value)
    {
        _store.SetSetting(SettingTolerance,
            value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        Reload();
    }

    partial void OnPlanOnlyChanged(bool value)
    {
        _store.SetSetting(SettingPlanOnly, value ? "1" : "0");
        Reload();
    }
}
