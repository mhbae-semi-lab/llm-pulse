using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;
using LLMPulse.Core;
using LLMPulse.Core.Plans;
using LLMPulse.Core.Scoring;

namespace LLMPulse.App.ViewModels;

public sealed class ModelScoreRow
{
    public required string CategoryId { get; init; }
    public required string Title { get; init; }
    public double Score { get; init; }
    public bool HasScore { get; init; }
    public string ScoreText { get; init; } = "";
    public string RankText { get; init; } = "";
    public bool IsTop { get; init; }
    public IReadOnlyList<double> Trend { get; init; } = [];
    public bool HasTrend => Trend.Count >= 2;
    public required IRelayCommand OpenCommand { get; init; }
}

public sealed class FactTile
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

public sealed class PlanAccessRow
{
    public required string Text { get; init; }
    public required string Glyph { get; init; }
    public required string Level { get; init; } // full | limited | none
}

/// <summary>모델 한 개의 상세: 분야별 점수/순위, 핵심 수치, 가격 추이, 내 구독에서의 접근 가능 여부.</summary>
public sealed partial class ModelDetailViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly Catalog _catalog;
    private readonly Loc _loc = Loc.Instance;
    private long _modelId;

    public ObservableCollection<ModelScoreRow> Scores { get; } = [];
    public ObservableCollection<FactTile> Facts { get; } = [];
    public ObservableCollection<PlanAccessRow> PlanRows { get; } = [];
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _found;
    [ObservableProperty] private bool _hasPlanRows;
    [ObservableProperty] private bool _hasPriceHistory;
    [ObservableProperty] private string _priceHistoryText = "";
    [ObservableProperty] private IReadOnlyList<double> _priceHistory = [];

    public IRelayCommand BackCommand { get; }
    public IRelayCommand ToggleFavoriteCommand { get; }
    public IRelayCommand OpenLeaderboardCommand { get; }

    public ModelDetailViewModel(AppState state, Catalog catalog)
    {
        _state = state;
        _catalog = catalog;
        BackCommand = new RelayCommand(() => state.Navigate(PageKey.Leaderboard));
        OpenLeaderboardCommand = new RelayCommand(() => state.Navigate(PageKey.Leaderboard));
        ToggleFavoriteCommand = new RelayCommand(() =>
        {
            state.SetFavorite(_modelId, !state.IsFavorite(_modelId));
            IsFavorite = state.IsFavorite(_modelId);
        });
        state.DataChanged += Rebuild;
        state.FavoritesChanged += () => IsFavorite = state.IsFavorite(_modelId);
        _loc.LanguageChanged += Rebuild;
    }

    public void Load(long modelId)
    {
        _modelId = modelId;
        Rebuild();
    }

    private void Rebuild()
    {
        Scores.Clear();
        Facts.Clear();
        PlanRows.Clear();
        var snap = _state.Snapshot;
        if (snap is null || !snap.Models.TryGetValue(_modelId, out var m))
        {
            Found = false;
            return;
        }

        Found = true;
        Name = m.Name;
        var sub = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.Vendor)) sub.Add(m.Vendor!);
        if (!string.IsNullOrWhiteSpace(m.ReleaseDate)) sub.Add(m.ReleaseDate!);
        Subtitle = string.Join(" · ", sub);
        IsFavorite = _state.IsFavorite(_modelId);

        // 핵심 수치
        void Fact(string labelKey, string value) => Facts.Add(new FactTile { Label = _loc[labelKey], Value = value });
        Fact("Col_Price", m.BlendedPrice is { } p ? Fmt.Price(p) + "/1M" : Fmt.Dash);
        Fact("Col_Speed", m.Metrics.TryGetValue("output_tok_s", out var tps) ? Fmt.Number(tps, "0.#") + " tok/s" : Fmt.Dash);
        Fact("Col_Ttft", m.Metrics.TryGetValue("ttft_s", out var ttft) ? Fmt.Number(ttft, "0.00") + " s" : Fmt.Dash);
        Fact("Model_Context", m.Metrics.TryGetValue("context_k", out var ctx) ? Fmt.Number(ctx, "#,0") + "K" : Fmt.Dash);

        // 분야별 점수
        var lang = _loc.Language;
        foreach (var cr in snap.Categories)
        {
            var s = cr.Scores.FirstOrDefault(x => x.ModelId == _modelId);
            var ranked = cr.Scores.Count(x => x.Rank is not null);
            var catId = cr.Category.Id;
            Scores.Add(new ModelScoreRow
            {
                CategoryId = catId,
                Title = cr.Category.Name.Get(lang),
                HasScore = s?.Score is not null,
                Score = s?.Score ?? 0,
                ScoreText = Fmt.Score(s?.Score),
                RankText = s?.Rank is { } r ? _loc.Format("Model_Rank", r, ranked) : Fmt.Dash,
                IsTop = s?.Rank == 1,
                Trend = s?.Score is null ? [] : _state.ScoreTrend(catId, _modelId),
                OpenCommand = new RelayCommand(() => _state.Navigate(PageKey.Category, catId)),
            });
        }

        // 내 구독에서의 접근
        foreach (var plan in _state.ChosenPlans)
        {
            var access = _state.AccessIn(plan, m);
            var (glyph, level, key) = access switch
            {
                PlanAccess.Full => ("\uE73E", "full", "Plans_AccessFull"),
                PlanAccess.Limited => ("\uE7BA", "limited", "Plans_AccessLimited"),
                _ => ("\uE711", "none", "Plans_AccessNone"),
            };
            PlanRows.Add(new PlanAccessRow { Text = $"{plan.Name.Get(lang)} - {_loc[key]}", Glyph = glyph, Level = level });
        }

        HasPlanRows = PlanRows.Count > 0;

        // 가격 이력
        var hist = _state.PriceHistory(_modelId);
        PriceHistory = hist;
        HasPriceHistory = hist.Count >= 2;
        PriceHistoryText = hist.Count >= 2
            ? _loc.Format("Model_PriceHistoryNote", hist.Count, Fmt.Price(hist[0]), Fmt.Price(hist[^1]))
            : "";
    }
}