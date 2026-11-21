using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;
using LLMPulse.Core;
using LLMPulse.Core.Scoring;

namespace LLMPulse.App.ViewModels;

public sealed record BarItem(long ModelId, string Name, double Score, bool InPlan = false, bool Favorite = false);
public sealed record PricePoint(long ModelId, string Name, double Price, double Score, bool Pareto, bool IsBest, bool IsValue, bool InPlan = true, bool Favorite = false);

public sealed partial class WeightRowViewModel : ObservableObject
{
    private readonly CategoryDetailViewModel _owner;
    public string MetricId { get; }
    [ObservableProperty] private string _name;
    public IRelayCommand InfoCommand { get; }

    /// <summary>가중치 0–100 (슬라이더). 0 이면 이 분야 계산에서 빠진다.</summary>
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _share = "";

    public WeightRowViewModel(CategoryDetailViewModel owner, string metricId, string name, double percent, IRelayCommand info)
    {
        _owner = owner;
        MetricId = metricId;
        _name = name;
        _percent = percent;
        InfoCommand = info;
    }

    public void Sync(double percent, string share)
    {
        _syncing = true;
        Percent = percent;
        Share = share;
        _syncing = false;
    }

    private bool _syncing;

    partial void OnPercentChanged(double value)
    {
        if (!_syncing) _owner.OnWeightEdited();
    }
}

public sealed class CategoryMetricRow
{
    public required string MetricId { get; init; }
    public required string Name { get; init; }
    public required string Weight { get; init; }
    public required string Direction { get; init; }
    public required IRelayCommand InfoCommand { get; init; }
}

public sealed partial class CategoryDetailViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly Catalog _catalog;
    private readonly Loc _loc = Loc.Instance;
    private string? _categoryId;

    public ObservableCollection<CategoryMetricRow> Metrics { get; } = [];
    public ObservableCollection<WeightRowViewModel> WeightRows { get; } = [];
    private bool _syncingWeights;

    /// <summary>지표가 2개 이상인 분야만 가중치 조정을 보여준다.</summary>
    [ObservableProperty] private bool _canTune;
    /// <summary>막대 차트에서 내 구독 모델을 따로 색칠하는 중인가.</summary>
    [ObservableProperty] private bool _showPlanLegend;
    [ObservableProperty] private bool _isCustomized;
    public IRelayCommand ResetWeightsCommand { get; }
    public ObservableCollection<string> Presets { get; } = [];
    [ObservableProperty] private string? _selectedPreset;
    [ObservableProperty] private string _newPresetName = "";
    [ObservableProperty] private bool _hasPresets;
    private bool _syncingPresets;
    public IRelayCommand SavePresetCommand { get; }
    public IRelayCommand DeletePresetCommand { get; }

    partial void OnSelectedPresetChanged(string? value)
    {
        if (_syncingPresets || value is null || _categoryId is null) return;
        _state.ApplyPreset(_categoryId, value);
    }

    private void RefreshPresets()
    {
        _syncingPresets = true;
        var keep = SelectedPreset;
        Presets.Clear();
        if (_categoryId is not null)
            foreach (var n in _state.PresetNames(_categoryId)) Presets.Add(n);
        HasPresets = Presets.Count > 0;
        SelectedPreset = keep is not null && Presets.Contains(keep) ? keep : null;
        _syncingPresets = false;
    }
    public IReadOnlyList<BarItem> Bars { get; private set; } = [];
    public IReadOnlyList<PricePoint> Points { get; private set; } = [];
    public void OpenModel(long id) => _state.Navigate(PageKey.Model, id.ToString());
    public bool ShowScatter { get; private set; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _bestLine = "";
    [ObservableProperty] private string _valueLine = "";
    [ObservableProperty] private bool _hasValueLine;
    [ObservableProperty] private string _rationale = "";
    [ObservableProperty] private bool _hasData;
    /// <summary>최근 출시 모델 중 이 분야 점수가 없어 순위에서 빠진 모델 안내 (없으면 빈 문자열).</summary>
    [ObservableProperty] private string _gapNote = "";

    /// <summary>차트를 다시 그려야 한다.</summary>
    public event Action? Updated;

    public IRelayCommand BackCommand { get; }
    public IRelayCommand OpenLeaderboardCommand { get; }

    public CategoryDetailViewModel(AppState state, Catalog catalog)
    {
        _state = state;
        _catalog = catalog;
        BackCommand = new RelayCommand(() => state.Navigate(PageKey.Dashboard));
        ResetWeightsCommand = new RelayCommand(() => { if (_categoryId is not null) state.ResetWeights(_categoryId); });
        SavePresetCommand = new RelayCommand(() =>
        {
            if (_categoryId is null || NewPresetName.Trim().Length == 0) return;
            state.SavePreset(_categoryId, NewPresetName, WeightRows.ToDictionary(r => r.MetricId, r => r.Percent / 100.0));
            _syncingPresets = true;
            RefreshPresets();
            SelectedPreset = Presets.FirstOrDefault(p => string.Equals(p, NewPresetName.Trim(), StringComparison.CurrentCultureIgnoreCase));
            _syncingPresets = false;
            NewPresetName = "";
        });
        DeletePresetCommand = new RelayCommand(() =>
        {
            if (_categoryId is null || SelectedPreset is null) return;
            state.DeletePreset(_categoryId, SelectedPreset);
            RefreshPresets();
        });
        state.PresetsChanged += RefreshPresets;
        OpenLeaderboardCommand = new RelayCommand(() =>
        {
            if (_categoryId is not null) state.Navigate(PageKey.Leaderboard, _categoryId);
        });
        state.DataChanged += Rebuild;
        state.FavoritesChanged += Rebuild;
        _loc.LanguageChanged += Rebuild;
    }

    public void Load(string categoryId)
    {
        _categoryId = categoryId;
        Rebuild();
    }

    internal void OnWeightEdited()
    {
        if (_categoryId is null || _syncingWeights) return;
        _state.SetWeights(_categoryId, WeightRows.ToDictionary(r => r.MetricId, r => r.Percent / 100.0));
    }

    private void BuildWeightRows()
    {
        var original = _categoryId is null ? null : _state.OriginalCategory(_categoryId);
        CanTune = original is { Metrics.Count: > 1 };
        IsCustomized = _categoryId is not null && _state.HasCustomWeights(_categoryId);
        if (original is null || !CanTune) { WeightRows.Clear(); return; }

        var lang = _loc.Language;
        var current = _state.CurrentWeights(original.Id);
        var total = current.Values.Where(w => w > 0).Sum();
        string Share(double w) => total > 0 && w > 0 ? Fmt.Percent(w / total, "0%") : "—";

        _syncingWeights = true;
        try
        {
            var ids = original.Metrics.Select(m => m.Id).ToList();
            if (!WeightRows.Select(r => r.MetricId).SequenceEqual(ids))
            {
                WeightRows.Clear();
                foreach (var cm in original.Metrics)
                {
                    var id = cm.Id;
                    var name = _catalog.MetricsById.TryGetValue(id, out var def) ? def.Name.Get(lang) : id;
                    WeightRows.Add(new WeightRowViewModel(this, id, name, current[id] * 100,
                        new RelayCommand(() => _state.Navigate(PageKey.Metrics, id))));
                }
            }

            foreach (var row in WeightRows)
            {
                row.Name = _catalog.MetricsById.TryGetValue(row.MetricId, out var d) ? d.Name.Get(lang) : row.MetricId;
                row.Sync(current[row.MetricId] * 100, Share(current[row.MetricId]));
            }
        }
        finally { _syncingWeights = false; }
    }

    private void Rebuild()
    {
        if (_categoryId is null) return;
        var snap = _state.Snapshot;
        var cr = snap?.Categories.FirstOrDefault(c => c.Category.Id == _categoryId);
        var lang = _loc.Language;

        Metrics.Clear();
        if (cr is null || snap is null)
        {
            HasData = false;
            Bars = [];
            Points = [];
            Updated?.Invoke();
            return;
        }

        var cat = cr.Category;
        Title = cat.Name.Get(lang);

        var totalWeight = cat.Metrics.Sum(m => m.Weight);
        foreach (var cm in cat.Metrics)
        {
            if (!_catalog.MetricsById.TryGetValue(cm.Id, out var def)) continue;
            var id = cm.Id;
            Metrics.Add(new CategoryMetricRow
            {
                MetricId = id,
                Name = def.Name.Get(lang),
                Weight = Fmt.Percent(totalWeight > 0 ? cm.Weight / totalWeight : 0, "0%"),
                Direction = def.HigherIsBetter ? _loc["Metric_HigherBetter"] : _loc["Metric_LowerBetter"],
                InfoCommand = new RelayCommand(() => _state.Navigate(PageKey.Metrics, id)),
            });
        }

        BuildWeightRows();
        RefreshPresets();

        // 제한 보기에서는 전부 내 구독 모델이므로 따로 강조하지 않는다
        ShowPlanLegend = _state.HasPlans && !_state.Restricting;

        var scoredIds = cr.Scores.Where(s => s.Score is not null).Select(s => s.ModelId).ToHashSet();
        var gaps = CoverageGaps.RecentUnscored(
            snap.Models.Values.Select(m => (m.Id, m.Name, m.ReleaseDate)), scoredIds,
            DateOnly.FromDateTime(DateTime.UtcNow), LeaderboardViewModel.NewWithinDays);
        GapNote = gaps.Count == 0 ? "" : _loc.Format("Category_GapNote", LeaderboardViewModel.NewWithinDays, gaps.Count.ToString("N0", _loc.Culture),
            string.Join(", ", gaps.Take(4)) + (gaps.Count > 4 ? ", …" : ""));

        var scored = cr.Scores.Where(s => s.Score is not null && (!_state.Restricting || _state.InPlan(s.ModelId))).OrderByDescending(s => s.Score).ToList();
        HasData = scored.Count > 0;

        Bars = scored.Take(12)
            .Select(s => new BarItem(s.ModelId, snap.Models[s.ModelId].Name, s.Score!.Value,
                InPlan: ShowPlanLegend && _state.InPlan(s.ModelId), Favorite: _state.IsFavorite(s.ModelId))).ToList();

        var pareto = cr.Pareto.ToHashSet();
        var rec = cr.Recommendation;
        ShowScatter = cat.ValueMode;
        Points = cat.ValueMode
            ? scored.Where(s => snap.Models[s.ModelId].BlendedPrice is not null)
                .Select(s => new PricePoint(
                    s.ModelId, snap.Models[s.ModelId].Name,
                    snap.Models[s.ModelId].BlendedPrice!.Value, s.Score!.Value,
                    pareto.Contains(s.ModelId),
                    rec.Best?.ModelId == s.ModelId,
                    rec.BestValue?.ModelId == s.ModelId,
                    _state.InPlan(s.ModelId), _state.IsFavorite(s.ModelId))).ToList()
            : [];

        BestLine = "";
        ValueLine = "";
        HasValueLine = false;
        Rationale = "";
        if (rec.Best is { } best)
        {
            var m = snap.Models[best.ModelId];
            BestLine = $"{m.Name} — {_loc["Card_Score"]} {Fmt.Score(best.Score)} · {Fmt.Price(m.BlendedPrice)}/1M";
            if (rec.BestValue is { } val)
            {
                var vm = snap.Models[val.ModelId];
                HasValueLine = true;
                ValueLine = $"{vm.Name} — {_loc["Card_Score"]} {Fmt.Score(val.Score)} · {Fmt.Price(rec.BestValuePrice)}/1M";
                var ratio = best.Score > 0 ? val.Score!.Value / best.Score!.Value : 1;
                Rationale = _loc.Format("Rationale_Value", Title, Fmt.Score(val.Score), Fmt.Percent(ratio),
                    Fmt.Score(best.Score), Fmt.Price(rec.BestValuePrice), Fmt.Percent(rec.Tolerance));
            }
            else
            {
                Rationale = _loc.Format("Rationale_Best", Title, Fmt.Score(best.Score),
                    scored.Count.ToString("N0", _loc.Culture));
            }
        }

        Updated?.Invoke();
    }
}
