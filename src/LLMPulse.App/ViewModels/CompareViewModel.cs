using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;
using LLMPulse.Core;
using LLMPulse.Core.Scoring;
using LLMPulse.Core.Storage;

namespace LLMPulse.App.ViewModels;

public sealed class CompareModel
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public string Vendor { get; init; } = "";
    /// <summary>범례 마커 문자 (색 + 모양 병행, 색약 고려).</summary>
    public required string Marker { get; init; }
    public required double?[] Scores { get; init; }
}

public sealed class CompareRow
{
    public required string Label { get; init; }
    public required IReadOnlyList<string> Values { get; init; }
    public bool IsHeader { get; init; }
}

public sealed record MetricOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class CompareViewModel : ObservableObject
{
    public const int MaxModels = 4;
    public static readonly string[] Markers = ["●", "■", "▲", "◆"];

    private readonly AppState _state;
    private readonly Catalog _catalog;
    private readonly SqliteStore _store;
    private readonly Loc _loc = Loc.Instance;
    private List<long> _ids = [];

    public ObservableCollection<CompareModel> Models { get; } = [];
    public ObservableCollection<CompareRow> Rows { get; } = [];
    public ObservableCollection<MetricOption> TrendMetrics { get; } = [];
    public IReadOnlyList<string> CategoryNames { get; private set; } = [];
    public IReadOnlyList<int> RadarAxes { get; private set; } = [];
    /// <summary>전문 분야(aa_*) 막대 비교용 분야 인덱스.</summary>
    public IReadOnlyList<int> DomainAxes { get; private set; } = [];

    [ObservableProperty] private MetricOption? _selectedTrendMetric;
    [ObservableProperty] private bool _hasModels;
    private bool _loadingTokens;
    [ObservableProperty] private string _monthlyTokensText = "30";

    private double MonthlyTokensM =>
        double.TryParse(MonthlyTokensText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v) && v > 0 ? v : 30;

    partial void OnMonthlyTokensTextChanged(string value)
    {
        if (_loadingTokens) return;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v) && v > 0)
            _state.SaveSetting(AppState.SettingMonthlyTokens, v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        Rebuild();
    }
    [ObservableProperty] private string _title = "";

    public event Action? Updated;
    public IRelayCommand BackCommand { get; }

    public CompareViewModel(AppState state, Catalog catalog, SqliteStore store)
    {
        _state = state;
        _catalog = catalog;
        _store = store;
        BackCommand = new RelayCommand(() => state.Navigate(PageKey.Leaderboard));
        _loadingTokens = true;
        _monthlyTokensText = double.TryParse(state.ReadSetting(AppState.SettingMonthlyTokens), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var savedTokens)
            ? savedTokens.ToString("0.###", Localization.Loc.Instance.Culture) : "30"; // 저장은 Invariant, 화면은 현재 언어 표기
        _loadingTokens = false;
        state.DataChanged += Rebuild;
        _loc.LanguageChanged += Rebuild;
    }

    /// <summary>"1,2,3" 형식의 모델 id (최대 4개).</summary>
    public void Load(string csv)
    {
        _ids = csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s, out var v) ? v : (long?)null)
            .Where(v => v is not null).Select(v => v!.Value).Distinct().Take(MaxModels).ToList();
        Rebuild();
    }

    partial void OnSelectedTrendMetricChanged(MetricOption? value) => Updated?.Invoke();

    public List<(DateOnly Date, double Value)> History(long modelId) =>
        SelectedTrendMetric is { } m ? _store.LoadHistory(modelId, m.Id) : [];

    private void Rebuild()
    {
        var snap = _state.Snapshot;
        var lang = _loc.Language;
        Models.Clear();
        Rows.Clear();

        var keepMetric = SelectedTrendMetric?.Id;
        TrendMetrics.Clear();
        foreach (var m in _catalog.Metrics.Where(m => m.Id != MetricKeys.BlendedPrice))
            TrendMetrics.Add(new MetricOption(m.Id, m.Name.Get(lang)));
        SelectedTrendMetric = TrendMetrics.FirstOrDefault(t => t.Id == keepMetric) ?? TrendMetrics.FirstOrDefault();

        CategoryNames = _catalog.Categories.Select(c => c.Name.Get(lang)).ToList();
        // 레이더에는 핵심 분야만 (전문 지수 aa_* 는 표에만 나온다. 축이 너무 많으면 읽을 수 없다)
        RadarAxes = Enumerable.Range(0, _catalog.Categories.Count)
            .Where(i => !_catalog.Categories[i].Id.StartsWith("aa_", StringComparison.Ordinal)).ToList();

        DomainAxes = Enumerable.Range(0, _catalog.Categories.Count)
            .Where(i => _catalog.Categories[i].Id.StartsWith("aa_", StringComparison.Ordinal)).ToList();

        if (snap is null)
        {
            HasModels = false;
            Updated?.Invoke();
            return;
        }

        var byCat = snap.Categories.Select(c => c.Scores.ToDictionary(s => s.ModelId, s => s.Score)).ToList();

        var i = 0;
        foreach (var id in _ids)
        {
            if (!snap.Models.TryGetValue(id, out var m)) continue;
            Models.Add(new CompareModel
            {
                Id = id,
                Name = m.Name,
                Vendor = m.Vendor ?? "",
                Marker = Markers[i++ % Markers.Length],
                Scores = byCat.Select(d => d.TryGetValue(id, out var s) ? s : null).ToArray(),
            });
        }

        HasModels = Models.Count > 0;
        Title = Models.Count == 1 ? Models[0].Name : _loc["Compare_Title"];

        // 표: 분야 점수 → 원본 값
        Rows.Add(new CompareRow { Label = _loc["Compare_Scores"], Values = [], IsHeader = true });
        for (var c = 0; c < _catalog.Categories.Count; c++)
        {
            var idx = c;
            Rows.Add(new CompareRow
            {
                Label = _catalog.Categories[c].Name.Get(lang),
                Values = Models.Select(m => Fmt.Score(m.Scores[idx])).ToList(),
            });
        }

        Rows.Add(new CompareRow { Label = _loc["Compare_Raw"], Values = [], IsHeader = true });
        ModelInput? Input(CompareModel m) => snap.Models.GetValueOrDefault(m.Id);
        Rows.Add(new CompareRow { Label = _loc["Col_Vendor"], Values = Models.Select(m => m.Vendor.Length > 0 ? m.Vendor : Fmt.Dash).ToList() });
        Rows.Add(new CompareRow { Label = _loc["Col_Release"], Values = Models.Select(m => Input(m)?.ReleaseDate ?? Fmt.Dash).ToList() });
        Rows.Add(new CompareRow { Label = _loc["Col_Price"], Values = Models.Select(m => Fmt.Price(Input(m)?.BlendedPrice)).ToList() });
        Rows.Add(new CompareRow
        {
            Label = _loc.Format("Compare_MonthlyCost", MonthlyTokensM.ToString("0.##", _loc.Culture)),
            Values = Models.Select(m => Input(m)?.BlendedPrice is { } p ? Fmt.Price(p * MonthlyTokensM) : Fmt.Dash).ToList(),
        });
        Rows.Add(new CompareRow { Label = _loc["Col_Speed"], Values = Models.Select(m => Fmt.Number(Raw(Input(m), "output_tok_s"))).ToList() });
        Rows.Add(new CompareRow { Label = _loc["Col_Ttft"], Values = Models.Select(m => Fmt.Number(Raw(Input(m), "ttft_s"), "0.00")).ToList() });

        Updated?.Invoke();
    }

    private static double? Raw(ModelInput? m, string id) =>
        m is not null && m.Metrics.TryGetValue(id, out var v) ? v : null;
}
