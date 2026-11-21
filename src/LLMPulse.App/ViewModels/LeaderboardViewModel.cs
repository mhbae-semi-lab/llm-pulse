using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LLMPulse.App.Localization;
using LLMPulse.Core;
using LLMPulse.Core.Export;
using LLMPulse.Core.Scoring;

namespace LLMPulse.App.ViewModels;

public sealed class LeaderboardRow : ObservableObject
{
    private bool _isFavorite;

    /// <summary>즐겨찾기 별. 사용자가 바꾸면 Toggled 로 알린다 (초기값 지정 시에는 Toggled 가 아직 null).</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (SetProperty(ref _isFavorite, value)) Toggled?.Invoke(ModelId, value); }
    }

    public Action<long, bool>? Toggled { get; init; }

    public required long ModelId { get; init; }
    public required string Name { get; init; }
    public string Vendor { get; init; } = "";
    public string Release { get; init; } = "";
    /// <summary>Catalog.Categories 순서와 같은 분야 점수(0–100). 없으면 null.</summary>
    public required double?[] Scores { get; init; }
    public double? Price { get; init; }
    public double? TokensPerSecond { get; init; }
    public double? Ttft { get; init; }
    public bool IsParetoAnywhere { get; init; }
    public double? ContextK { get; init; }
    /// <summary>출시 30일 이내 (UTC 기준) — 이름 옆에 NEW 배지.</summary>
    public bool IsNew { get; init; }
    public string AccessGlyph { get; init; } = "";
    public System.Windows.Media.Brush AccessBrush { get; init; } = System.Windows.Media.Brushes.Gray;
    public string AccessTip { get; init; } = "";
    public int AccessOrder { get; init; }
    /// <summary>소스 간 불일치 건수와 설명 (없으면 0 / 빈 문자열).</summary>
    public int DisagreeCount { get; init; }
    public string DisagreeGlyph => DisagreeCount > 0 ? "" : "";
    public string? DisagreeTip { get; init; }
}

/// <summary>필터 드롭다운 항목 (Value == null 이면 "제한 없음").</summary>
public sealed record FilterOption(string Label, double? Value)
{
    public override string ToString() => Label;
}

public sealed partial class LeaderboardViewModel : ObservableObject
{
    /// <summary>"최근 출시" 로 보는 기간(일).</summary>
    public const int NewWithinDays = 30;
    private static readonly double?[] PriceSteps = [null, 1, 5, 15, 50];
    private static readonly double?[] ContextSteps = [null, 128, 200, 1000];


    private readonly AppState _state;
    private readonly Loc _loc = Loc.Instance;
    private List<LeaderboardRow> _all = [];

    public Catalog Catalog { get; }
    public ObservableCollection<LeaderboardRow> Rows { get; } = [];
    public ObservableCollection<string> Vendors { get; } = [];

    [ObservableProperty] private string? _selectedVendor;
    [ObservableProperty] private bool _favoritesOnly;
    [ObservableProperty] private bool _recentOnly;
    public ObservableCollection<FilterOption> PriceOptions { get; } = [];
    public ObservableCollection<FilterOption> ContextOptions { get; } = [];
    [ObservableProperty] private FilterOption? _maxPrice;
    [ObservableProperty] private FilterOption? _minContext;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _compareText = "";

    private List<long> _selectedIds = [];

    public System.Windows.Input.ICommand CompareCommand { get; }

    /// <summary>선택된 모델 id 갱신 (View 의 DataGrid 선택에서 호출).</summary>
    public void SetSelection(IEnumerable<long> ids)
    {
        _selectedIds = ids.ToList();
        UpdateCompareText();
        (CompareCommand as CommunityToolkit.Mvvm.Input.RelayCommand)?.NotifyCanExecuteChanged();
    }

    /// <summary>현재 화면(필터·정렬 적용)의 행을 CSV 로. 숫자는 문화권과 무관한 형식.</summary>
    public string BuildCsv()
    {
        var lang = _loc.Language;
        var header = new List<string> { _loc["Col_Model"], _loc["Col_Vendor"], _loc["Col_Release"] };
        header.AddRange(Catalog.Categories.Select(c => c.Name.Get(lang)));
        // 화살표(↑↓)는 화면용 표시라 CSV 헤더에서는 뺀다
        header.AddRange(new[] { "Col_Price", "Col_Speed", "Col_Ttft" }.Select(k => _loc[k].Trim(' ', '↑', '↓')));

        var rows = Rows.Select(r =>
        {
            var cells = new List<string> { CsvWriter.Text(r.Name), CsvWriter.Text(r.Vendor), r.Release };
            cells.AddRange(r.Scores.Select(s => CsvWriter.Number(s, "0.0")));
            cells.Add(CsvWriter.Number(r.Price));
            cells.Add(CsvWriter.Number(r.TokensPerSecond));
            cells.Add(CsvWriter.Number(r.Ttft));
            return (IReadOnlyList<string>)cells;
        });
        return CsvWriter.Build(header, rows);
    }

    /// <summary>불일치 한 건을 화면 문장으로 (모델 상세에서도 씀).</summary>
    public static string DisagreeText(LLMPulse.Core.Disagreement d)
    {
        var loc = Loc.Instance;
        return d.Kind == LLMPulse.Core.DisagreementKind.Price
            ? loc.Format("Disagree_Price", Fmt.Price(d.A), Fmt.Price(d.B), d.Gap.ToString("0", loc.Culture))
            : loc.Format("Disagree_Rank", d.A.ToString("0", loc.Culture), d.B.ToString("0", loc.Culture), d.Gap.ToString("0", loc.Culture));
    }

    public void OpenModel(long id) => _state.Navigate(PageKey.Model, id.ToString());

    public bool HasPlans => _state.HasPlans;
    public AppState State => _state;

    private const string SettingFavOnly = "lb_fav_only";
    private const string SettingRecentOnly = "lb_recent_only";
    private const string SettingMaxPrice = "lb_max_price";
    private const string SettingMinContext = "lb_min_context";
    private const string SettingVendor = "lb_vendor";
    private bool _rebuilding;

    private void UpdateCompareText() =>
        CompareText = _selectedIds.Count switch
        {
            0 => _loc["Leaderboard_CompareHint"],
            > CompareViewModel.MaxModels => _loc.Format("Leaderboard_CompareTooMany", CompareViewModel.MaxModels),
            _ => _loc.Format("Leaderboard_Compare", _selectedIds.Count),
        };

    /// <summary>대시보드 카드 클릭으로 들어온 경우 정렬할 분야 (View 가 소비).</summary>
    public string? PendingSortCategoryId { get; set; }

    public string SortKey { get; private set; } = "cat:0";
    public bool SortDescending { get; private set; } = true;

    /// <summary>같은 키를 다시 누르면 방향 토글, 새 키는 숫자는 내림차순/텍스트는 오름차순으로 시작.</summary>
    public void SetSort(string key, bool? descending = null)
    {
        if (descending is { } d) SortDescending = d;
        else if (key == SortKey) SortDescending = !SortDescending;
        else SortDescending = key is not ("name" or "vendor") && key != "ttft" && key != "price";
        SortKey = key;
        ApplyFilter();
    }

    public void OpenMetricInfo(string metricId) => _state.Navigate(PageKey.Metrics, metricId);

    private static double? Key(LeaderboardRow r, string key) => key switch
    {
        "price" => r.Price,
        "disagree" => r.DisagreeCount > 0 ? r.DisagreeCount : null,
        "tok" => r.TokensPerSecond,
        "ttft" => r.Ttft,
        _ when key.StartsWith("cat:") => r.Scores[int.Parse(key[4..])],
        _ => null,
    };

    /// <summary>행/열이 다시 만들어졌다 (언어·데이터 변경) — View 가 컬럼을 재구성.</summary>
    public event Action? Rebuilt;

    public LeaderboardViewModel(AppState state, Catalog catalog)
    {
        _state = state;
        Catalog = catalog;
        CompareCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(
            () => _state.Navigate(PageKey.Compare, string.Join(",", _selectedIds)),
            () => _selectedIds.Count is >= 1 and <= CompareViewModel.MaxModels);
        UpdateCompareText();
        state.DataChanged += Rebuild;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.SearchText)) ApplyFilter();
        };
        _loc.LanguageChanged += Rebuild;
        // 마지막 필터 상태 복원 (필드로 직접 넣어 아직 비어 있는 목록에 필터가 돌지 않게 한다)
        _favoritesOnly = _state.GetFlag(SettingFavOnly, false);
        _recentOnly = _state.GetFlag(SettingRecentOnly, false);
        _selectedVendor = _state.ReadSetting(SettingVendor) is { Length: > 0 } sv ? sv : null;
        Rebuild();
    }

    partial void OnSelectedVendorChanged(string? value)
    {
        if (!_rebuilding && value is not null)
            _state.SaveSetting(SettingVendor, value == Vendors.FirstOrDefault() ? "" : value);
        ApplyFilter();
    }

    partial void OnRecentOnlyChanged(bool value)
    {
        _state.SetFlag(SettingRecentOnly, value);
        ApplyFilter();
    }

    partial void OnMaxPriceChanged(FilterOption? value)
    {
        if (!_rebuilding) _state.SaveSetting(SettingMaxPrice, value?.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        ApplyFilter();
    }

    partial void OnMinContextChanged(FilterOption? value)
    {
        if (!_rebuilding) _state.SaveSetting(SettingMinContext, value?.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        ApplyFilter();
    }

    private FilterOption Pick(IList<FilterOption> options, string key) =>
        double.TryParse(_state.ReadSetting(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? options.FirstOrDefault(o => o.Value == v) ?? options[0]
            : options[0];

    partial void OnFavoritesOnlyChanged(bool value)
    {
        _state.SetFlag(SettingFavOnly, value);
        ApplyFilter();
    }

    // ---- 컬럼 표시 설정 (저장됨) ----

    /// <summary>처음에는 분야별 전문 지수(금융·법률 등)를 숨겨 두고, 사용자가 컬럼 메뉴에서 켠다.</summary>
    private const string DefaultHidden = "cat:aa_finance,cat:aa_strategy,cat:aa_legal,cat:aa_healthcare,cat:aa_engineering,cat:aa_economics";

    private HashSet<string> HiddenColumns() =>
        (_state.ReadSetting(AppState.SettingHiddenColumns) ?? DefaultHidden)
        .Split(",", StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    public bool IsColumnHidden(string key) => HiddenColumns().Contains(key);

    public void SetColumnHidden(string key, bool hidden)
    {
        var set = HiddenColumns();
        if (!(hidden ? set.Add(key) : set.Remove(key))) return;
        _state.SaveSetting(AppState.SettingHiddenColumns, string.Join(",", set.Order()));
        Rebuilt?.Invoke();
    }

    private void Rebuild()
    {
        UpdateCompareText();
        HasData = _state.HasData;
        var snap = _state.Snapshot;
        _all = [];

        if (snap is not null)
        {
            var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
            var paretoIds = snap.Categories.SelectMany(c => c.Pareto).ToHashSet();
            var byCat = snap.Categories.ToDictionary(c => c.Category.Id,
                c => c.Scores.ToDictionary(s => s.ModelId, s => s.Score));

            _all = snap.Models.Values.Select(m =>
            {
            var access = _state.AccessOf(m);
            var dis = _state.Disagreements.GetValueOrDefault(m.Id) ?? [];
            var (glyph, brush, key, order) = access switch
            {
                LLMPulse.Core.Plans.PlanAccess.Full => ("\uE73E", Solid(0x4C, 0xB5, 0x6B), "Plans_AccessFull", 0),
                LLMPulse.Core.Plans.PlanAccess.Limited => ("\uE7BA", Solid(0xE5, 0xA0, 0x2D), "Plans_AccessLimited", 1),
                _ => ("\uE711", Solid(0xD9, 0x5C, 0x5C), "Plans_AccessNone", 2),
            };
            return new LeaderboardRow
            {
                AccessGlyph = _state.HasPlans ? glyph : "",
                AccessBrush = brush,
                AccessTip = _state.HasPlans ? _loc[key] : "",
                AccessOrder = order,
                DisagreeCount = dis.Count,
                DisagreeTip = dis.Count > 0 ? string.Join(Environment.NewLine, dis.Select(DisagreeText)) : null,
                ModelId = m.Id,
                Name = m.Name,
                Vendor = m.Vendor ?? "",
                Release = m.ReleaseDate ?? "",
                Scores = Catalog.Categories
                    .Select(c => byCat.TryGetValue(c.Id, out var d) && d.TryGetValue(m.Id, out var s) ? s : null)
                    .ToArray(),
                Price = m.BlendedPrice,
                TokensPerSecond = m.Metrics.TryGetValue("output_tok_s", out var t) ? t : null,
                Ttft = m.Metrics.TryGetValue("ttft_s", out var f) ? f : null,
                IsParetoAnywhere = paretoIds.Contains(m.Id),
                ContextK = m.Metrics.TryGetValue("context_k", out var cx) ? cx : null,
                IsNew = ReleaseDates.IsRecent(m.ReleaseDate, todayUtc, NewWithinDays),
                IsFavorite = _state.IsFavorite(m.Id),
                Toggled = OnFavoriteToggled,
            };
            }).ToList();
        }

        _rebuilding = true;
        var keep = SelectedVendor;
        Vendors.Clear();
        Vendors.Add(_loc["Leaderboard_AllVendors"]);
        foreach (var v in _all.Select(r => r.Vendor).Where(v => v.Length > 0).Distinct().OrderBy(v => v))
            Vendors.Add(v);
        SelectedVendor = keep is not null && Vendors.Contains(keep) ? keep : Vendors[0];

        // 가격·컨텍스트 필터 목록 (라벨은 언어마다 다름). 현재 선택은 값으로 복원한다.
        var keepPrice = MaxPrice?.Value; var keepCtx = MinContext?.Value;
        var first = MaxPrice is null; // 첫 구성이면 저장된 값에서 복원
        PriceOptions.Clear();
        foreach (var v in PriceSteps)
            PriceOptions.Add(new FilterOption(v is null ? _loc["Leaderboard_AnyPrice"] : _loc.Format("Leaderboard_PriceUpTo", v.Value.ToString("0", _loc.Culture)), v));
        ContextOptions.Clear();
        foreach (var v in ContextSteps)
            ContextOptions.Add(new FilterOption(v is null ? _loc["Leaderboard_AnyContext"] : _loc.Format("Leaderboard_ContextAtLeast", v.Value >= 1000 ? "1M" : $"{v.Value:0}K"), v));
        MaxPrice = first ? Pick(PriceOptions, SettingMaxPrice) : PriceOptions.FirstOrDefault(o => o.Value == keepPrice) ?? PriceOptions[0];
        MinContext = first ? Pick(ContextOptions, SettingMinContext) : ContextOptions.FirstOrDefault(o => o.Value == keepCtx) ?? ContextOptions[0];
        _rebuilding = false;

        ApplyFilter();
        Rebuilt?.Invoke();
    }

    private static System.Windows.Media.SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private void OnFavoriteToggled(long id, bool value)
    {
        _state.SetFavorite(id, value);
        if (FavoritesOnly && !value) ApplyFilter(); // 보기 목록에서 바로 사라지게
    }

    private void ApplyFilter()
    {
        var q = _state.SearchText?.Trim() ?? "";
        var vendor = SelectedVendor is not null && SelectedVendor != Vendors.FirstOrDefault() ? SelectedVendor : null;

        var filtered = _all.Where(r =>
            (vendor is null || r.Vendor == vendor) &&
            (!_state.Restricting || _state.InPlan(r.ModelId)) &&
            (!FavoritesOnly || r.IsFavorite) &&
            ModelFilter.Passes(r.Price, r.ContextK, r.IsNew, MaxPrice?.Value, MinContext?.Value, RecentOnly) &&
            (q.Length == 0 ||
             r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             r.Vendor.Contains(q, StringComparison.OrdinalIgnoreCase)));

        var list = filtered.ToList();
        list = SortKey switch
        {
            "name" => (SortDescending ? list.OrderByDescending(r => r.Name) : list.OrderBy(r => r.Name)).ToList(),
            "vendor" => (SortDescending ? list.OrderByDescending(r => r.Vendor) : list.OrderBy(r => r.Vendor)).ToList(),
            "access" => list.OrderBy(r => SortDescending ? -r.AccessOrder : r.AccessOrder).ThenBy(r => r.Name).ToList(),
            "release" => (SortDescending ? list.OrderByDescending(r => r.Release) : list.OrderBy(r => r.Release)).ToList(),
            _ => // 숫자 열: 값 없는 행은 항상 뒤로
                list.OrderBy(r => Key(r, SortKey) is null)
                    .ThenBy(r => SortDescending ? -(Key(r, SortKey) ?? 0) : (Key(r, SortKey) ?? 0))
                    .ThenBy(r => r.Name).ToList(),
        };

        Rows.Clear();
        foreach (var r in list) Rows.Add(r);
        CountText = _loc.Format("Leaderboard_Count",
            Rows.Count.ToString("N0", _loc.Culture), _all.Count.ToString("N0", _loc.Culture));
    }
}
