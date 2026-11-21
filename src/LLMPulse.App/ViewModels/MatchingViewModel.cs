using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;
using LLMPulse.Core.Matching;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;

namespace LLMPulse.App.ViewModels;

public sealed class MatchItemViewModel
{
    public required string Source { get; init; }
    public required string SourceName { get; init; }
    public required string DisplayName { get; init; }
    public required string Detail { get; init; }
    public required string Status { get; init; }
    public required bool IsLinked { get; init; }
    public long? ModelId { get; init; }
}

public sealed record ModelChoiceViewModel(long Id, string Name, string Hint)
{
    public override string ToString() => Name;
}

/// <summary>M5: 보강 소스(OpenRouter) 항목과 기존 모델의 이름 매칭을 확인/수정하는 화면.</summary>
public sealed partial class MatchingViewModel : ObservableObject
{
    private static readonly string[] Sources = [OpenRouterAdapter.Source, AiderAdapter.Source, LmArenaAdapter.Source];
    private const int MaxListed = 300;
    private const int MaxChoices = 60;

    private readonly AppState _state;
    private readonly SqliteStore _store;
    private readonly Loc _loc = Loc.Instance;
    private List<SourceItemRow> _rows = [];
    private List<(long Id, string Name)> _models = [];

    public ObservableCollection<MatchItemViewModel> Items { get; } = [];
    public ObservableCollection<ModelChoiceViewModel> Suggestions { get; } = [];
    public ObservableCollection<ModelChoiceViewModel> Choices { get; } = [];
    public IReadOnlyList<string> Filters { get; private set; } = [];

    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _modelSearch = "";
    [ObservableProperty] private MatchItemViewModel? _selected;
    [ObservableProperty] private ModelChoiceViewModel? _selectedModel;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _listNote = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _hasItems;

    public bool HasSelection => Selected is not null;
    public bool SelectedIsLinked => Selected?.IsLinked == true;
    public bool HasSuggestions => Suggestions.Count > 0;

    public IRelayCommand LinkCommand { get; }
    public IRelayCommand UnlinkCommand { get; }
    public IRelayCommand<ModelChoiceViewModel> LinkSuggestionCommand { get; }

    public MatchingViewModel(AppState state, SqliteStore store)
    {
        _state = state;
        _store = store;
        LinkCommand = new RelayCommand(() => Link(SelectedModel), () => Selected is not null && SelectedModel is not null);
        UnlinkCommand = new RelayCommand(Unlink, () => Selected?.IsLinked == true);
        LinkSuggestionCommand = new RelayCommand<ModelChoiceViewModel>(Link);

        state.DataChanged += Reload;
        _loc.LanguageChanged += Reload;
        Reload();
    }

    partial void OnFilterIndexChanged(int value) => RebuildItems();
    partial void OnSearchChanged(string value) => RebuildItems();
    partial void OnModelSearchChanged(string value) => RebuildChoices();

    partial void OnSelectedChanged(MatchItemViewModel? value)
    {
        Message = "";
        SelectedModel = null;
        RebuildSuggestions();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedIsLinked));
        LinkCommand.NotifyCanExecuteChanged();
        UnlinkCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModelChanged(ModelChoiceViewModel? value) => LinkCommand.NotifyCanExecuteChanged();

    private void Reload()
    {
        _rows = Sources.SelectMany(_store.SourceItems).ToList();
        _models = _store.ModelChoices();
        Filters =
        [
            _loc["Matching_Filter_Unmatched"], _loc["Matching_Filter_Matched"], _loc["Matching_Filter_All"],
        ];
        OnPropertyChanged(nameof(Filters));
        var idx = FilterIndex;
        FilterIndex = -1;
        FilterIndex = idx < 0 ? 0 : idx; // ComboBox 항목 텍스트 갱신
        RebuildChoices();
    }

    private void RebuildItems()
    {
        if (FilterIndex < 0) return;
        var selectedKey = Selected is { } sel ? (sel.Source, sel.SourceName) : default;

        var matched = _rows.Count(r => r.ModelId is not null);
        Summary = _rows.Count == 0 ? "" : _loc.Format("Matching_Summary", matched, _rows.Count - matched);
        HasItems = _rows.Count > 0;

        var q = Search.Trim();
        var filtered = _rows.Where(r => FilterIndex switch
            {
                0 => r.ModelId is null,
                1 => r.ModelId is not null,
                _ => true,
            })
            .Where(r => q.Length == 0
                || r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.SourceName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.ModelName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        Items.Clear();
        foreach (var r in filtered.Take(MaxListed)) Items.Add(ToVm(r));
        ListNote = filtered.Count > MaxListed ? _loc.Format("Matching_Truncated", MaxListed, filtered.Count) : "";

        Selected = Items.FirstOrDefault(i => (i.Source, i.SourceName) == selectedKey);
    }

    private MatchItemViewModel ToVm(SourceItemRow r)
    {
        var parts = new List<string>();
        foreach (var (id, value) in r.Metrics)
            parts.Add(id switch
            {
                MetricIds.ContextK => _loc.Format("Matching_Context", Fmt.Number(value, "#,0")),
                MetricIds.AiderPolyglot => _loc.Format("Matching_Aider", Fmt.Number(value, "0.#")),
                _ => $"{id}: {Fmt.Number(value, "0.##")}",
            });
        if (r.InputPerM is not null || r.OutputPerM is not null)
            parts.Add(_loc.Format("Matching_Price", Fmt.Price(r.InputPerM), Fmt.Price(r.OutputPerM)));

        var label = r.Source == AiderAdapter.Source ? "Aider" : r.Source == LmArenaAdapter.Source ? "Arena" : "OpenRouter";
        return new MatchItemViewModel
        {
            Source = r.Source,
            SourceName = r.SourceName,
            DisplayName = r.DisplayName,
            Detail = string.Join(" · ", parts),
            IsLinked = r.ModelId is not null,
            ModelId = r.ModelId,
            Status = label + " · " + (r.ModelName is { } n ? "→ " + n : _loc["Matching_Status_Needed"]),
        };
    }

    private void RebuildSuggestions()
    {
        Suggestions.Clear();
        if (Selected is { } s)
        {
            var row = _rows.FirstOrDefault(r => r.Source == s.Source && r.SourceName == s.SourceName);
            if (row is not null)
                foreach (var x in AliasResolver.Suggest(row.DisplayName, _models))
                    Suggestions.Add(new ModelChoiceViewModel(x.Id, x.Name, Fmt.Percent(x.Score, "0%")));
        }

        OnPropertyChanged(nameof(HasSuggestions));
    }

    private void RebuildChoices()
    {
        var q = ModelSearch.Trim();
        Choices.Clear();
        foreach (var m in _models
                     .Where(m => q.Length == 0 || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .Take(MaxChoices))
            Choices.Add(new ModelChoiceViewModel(m.Id, m.Name, ""));
    }

    private void Link(ModelChoiceViewModel? model)
    {
        if (Selected is not { } item || model is null) return;
        var name = item.SourceName;
        var key = (item.Source, name);
        try
        {
            _store.LinkAlias(item.Source, name, model.Id);
            Message = _loc.Format("Matching_Linked", item.DisplayName, model.Name);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            return;
        }

        AfterChange(key);
    }

    private void Unlink()
    {
        if (Selected is not { } item) return;
        _store.UnlinkAlias(item.Source, item.SourceName);
        Message = _loc.Format("Matching_Unlinked", item.DisplayName);
        AfterChange((item.Source, item.SourceName));
    }

    private void AfterChange((string Source, string SourceName) keepSelected)
    {
        var msg = Message;
        _state.Reload(); // 점수/추천 재계산 → DataChanged → Reload()
        Selected = Items.FirstOrDefault(i => (i.Source, i.SourceName) == keepSelected);
        Message = msg;
    }
}