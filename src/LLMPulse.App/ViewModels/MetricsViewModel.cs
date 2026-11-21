using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LLMPulse.App.Localization;
using LLMPulse.Core;

namespace LLMPulse.App.ViewModels;

public sealed class MetricCardViewModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required string Method { get; init; }
    public required string Scale { get; init; }
    public required string Direction { get; init; }
    public required string Source { get; init; }
    public required string SourceUrl { get; init; }
    public required string Updated { get; init; }
    public required string Caveats { get; init; }
    public required string UsedIn { get; init; }
}

public sealed partial class MetricsViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly Catalog _catalog;
    private readonly Loc _loc = Loc.Instance;

    public ObservableCollection<MetricCardViewModel> Cards { get; } = [];

    [ObservableProperty] private MetricCardViewModel? _selected;
    [ObservableProperty] private string _scoringExample = "";

    /// <summary>선택된 카드로 스크롤하도록 View 에 알림.</summary>
    public event Action<MetricCardViewModel>? ScrollRequested;

    public MetricsViewModel(AppState state, Catalog catalog)
    {
        _state = state;
        _catalog = catalog;
        state.DataChanged += Rebuild;
        _loc.LanguageChanged += Rebuild;
        Rebuild();
    }

    public void Select(string metricId)
    {
        var card = Cards.FirstOrDefault(c => c.Id == metricId);
        if (card is null) return;
        Selected = card;
        ScrollRequested?.Invoke(card);
    }

    private void Rebuild()
    {
        var keep = Selected?.Id;
        var lang = _loc.Language;
        Cards.Clear();

        foreach (var m in _catalog.Metrics)
        {
            var usedIn = string.Join(", ", _catalog.Categories
                .Where(c => c.Metrics.Any(x => x.Id == m.Id))
                .Select(c => c.Name.Get(lang)));

            Cards.Add(new MetricCardViewModel
            {
                Id = m.Id,
                Name = m.Name.Get(lang),
                Summary = m.Summary.Get(lang),
                Method = m.Method.Get(lang),
                Scale = m.IsPassthrough
                    ? _loc.Format("Metric_ScaleIndex", m.Unit)
                    : _loc.Format("Metric_ScalePercentile", m.Unit),
                Direction = m.HigherIsBetter ? _loc["Metric_HigherBetter"] : _loc["Metric_LowerBetter"],
                Source = m.Source,
                SourceUrl = m.SourceUrl,
                Updated = Fmt.Date(_state.LastRefreshAt),
                Caveats = m.Caveats.Get(lang),
                UsedIn = usedIn,
            });
        }

        ScoringExample = _loc.Format("Scoring_Example", Fmt.Percent(_state.Tolerance, "0.#%"));
        if (keep is not null) Selected = Cards.FirstOrDefault(c => c.Id == keep);
    }
}
