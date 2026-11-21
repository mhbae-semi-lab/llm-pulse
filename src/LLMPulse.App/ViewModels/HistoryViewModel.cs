using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;

namespace LLMPulse.App.ViewModels;

public sealed record CategoryChoice(string Id, string Title);

public sealed class SnapshotRow
{
    public required string Name { get; init; }
    public required string RankText { get; init; }
    public required string DeltaText { get; init; }
    public int Delta { get; init; }
    public System.Windows.Media.Brush DeltaBrush => Delta > 0 ? Up : Delta < 0 ? Down : System.Windows.Media.Brushes.Gray;
    private static readonly System.Windows.Media.Brush Up = Freeze(0x4C, 0xB5, 0x6B);
    private static readonly System.Windows.Media.Brush Down = Freeze(0xD9, 0x5C, 0x5C);
    private static System.Windows.Media.Brush Freeze(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
    public required string ScoreText { get; init; }
    public required string PriceText { get; init; }
    public required IRelayCommand OpenCommand { get; init; }
}

public sealed class ReleaseRow
{
    public required string Date { get; init; }
    public required string Name { get; init; }
    public required string Vendor { get; init; }
    public required string ScoreText { get; init; }
    public bool IsRecord { get; init; }
    public required IRelayCommand OpenCommand { get; init; }
}

public readonly record struct ReleasePoint(DateOnly Date, double Score, bool IsRecord);

/// <summary>스냅샷 비교(두 날짜의 순위·가격 변화)와 모델 출시 타임라인(세대별 최고 성능 변화).</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly Loc _loc = Loc.Instance;
    private bool _building;

    public ObservableCollection<string> Dates { get; } = [];
    public ObservableCollection<CategoryChoice> Categories { get; } = [];
    public ObservableCollection<SnapshotRow> Rows { get; } = [];
    public ObservableCollection<ReleaseRow> Releases { get; } = [];
    public IReadOnlyList<ReleasePoint> ReleasePoints { get; private set; } = [];

    [ObservableProperty] private string? _fromDate;
    [ObservableProperty] private string? _toDate;
    [ObservableProperty] private CategoryChoice? _selectedCategory;
    [ObservableProperty] private string _compareNote = "";
    [ObservableProperty] private string _releaseNote = "";
    [ObservableProperty] private bool _hasHistory;

    /// <summary>타임라인 차트 다시 그리기 요청.</summary>
    public event Action? TimelineChanged;

    public HistoryViewModel(AppState state)
    {
        _state = state;
        state.DataChanged += Rebuild;
        _loc.LanguageChanged += Rebuild;
        Rebuild();
    }

    partial void OnFromDateChanged(string? value) => BuildCompare();
    partial void OnToDateChanged(string? value) => BuildCompare();
    partial void OnSelectedCategoryChanged(CategoryChoice? value) => BuildCompare();

    private void Rebuild()
    {
        _building = true;
        var oldFrom = FromDate; var oldTo = ToDate; var oldCat = SelectedCategory?.Id;
        Dates.Clear();
        foreach (var d in _state.ScoreDateList()) Dates.Add(d);
        HasHistory = Dates.Count >= 2;

        Categories.Clear();
        var lang = _loc.Language;
        if (_state.Snapshot is { } snap)
            foreach (var c in snap.Categories) Categories.Add(new CategoryChoice(c.Category.Id, c.Category.Name.Get(lang)));

        ToDate = oldTo is not null && Dates.Contains(oldTo) ? oldTo : Dates.FirstOrDefault();
        FromDate = oldFrom is not null && Dates.Contains(oldFrom) ? oldFrom : Dates.Skip(1).FirstOrDefault();
        SelectedCategory = Categories.FirstOrDefault(c => c.Id == oldCat) ?? Categories.FirstOrDefault();
        _building = false;

        BuildCompare();
        BuildReleases();
    }

    private void BuildCompare()
    {
        if (_building) return;
        Rows.Clear();
        CompareNote = "";
        var snap = _state.Snapshot;
        if (snap is null || SelectedCategory is null || FromDate is null || ToDate is null || Dates.Count < 2)
        {
            CompareNote = _loc["History_NeedTwo"];
            return;
        }

        var from = _state.CategoryScoresOn(SelectedCategory.Id, FromDate);
        var to = _state.CategoryScoresOn(SelectedCategory.Id, ToDate);
        var p0 = _state.PricesAsOf(FromDate);
        var p1 = _state.PricesAsOf(ToDate);

        var list = new List<SnapshotRow>();
        foreach (var id in to.Keys.Union(from.Keys))
        {
            if (!snap.Models.TryGetValue(id, out var m)) continue;
            if (_state.Restricting && !_state.InPlan(id)) continue;
            from.TryGetValue(id, out var a);
            to.TryGetValue(id, out var b);
            var r0 = a.Rank; var r1 = b.Rank;
            var delta = r0 is { } x && r1 is { } y ? x - y : 0;
            var priceChanged = p0.TryGetValue(id, out var pa) && p1.TryGetValue(id, out var pb) && pa > 0
                               && Math.Abs(pb - pa) / pa >= 0.05;
            if (delta == 0 && !priceChanged && !(r0 is null ^ r1 is null)) continue;

            var priceText = "";
            if (p0.TryGetValue(id, out var q0) && p1.TryGetValue(id, out var q1))
                priceText = q0 == 0 || Math.Abs(q1 - q0) / q0 < 0.005
                    ? Fmt.Price(q1)
                    : $"{Fmt.Price(q0)} → {Fmt.Price(q1)} ({(q1 - q0) / q0:+0%;-0%})";
            else if (p1.TryGetValue(id, out var only)) priceText = Fmt.Price(only);

            var modelId = id;
            list.Add(new SnapshotRow
            {
                Name = m.Name,
                RankText = $"{(r0 is { } ra ? "#" + ra : Fmt.Dash)} → {(r1 is { } rb ? "#" + rb : Fmt.Dash)}",
                Delta = delta,
                DeltaText = r0 is null && r1 is not null ? _loc["History_New"] : delta > 0 ? $"▲{delta}" : delta < 0 ? $"▼{-delta}" : "–",
                ScoreText = Fmt.Score(b.Score),
                PriceText = priceText,
                OpenCommand = new RelayCommand(() => _state.Navigate(PageKey.Model, modelId.ToString())),
            });
        }

        foreach (var r in list.OrderByDescending(r => Math.Abs(r.Delta)).ThenBy(r => r.Name).Take(80)) Rows.Add(r);
        if (Rows.Count == 0) CompareNote = _loc["History_NoChanges"];
    }

    private void BuildReleases()
    {
        Releases.Clear();
        ReleaseNote = "";
        var snap = _state.Snapshot;
        var overall = snap?.Categories.FirstOrDefault(c => c.Category.Id == "overall") ?? snap?.Categories.FirstOrDefault();
        if (snap is null || overall is null)
        {
            ReleasePoints = [];
            TimelineChanged?.Invoke();
            return;
        }

        var scores = overall.Scores.Where(s => s.Score is not null).ToDictionary(s => s.ModelId, s => s.Score!.Value);
        var dated = snap.Models.Values
            .Select(m => (Model: m, Date: DateOnly.TryParse(m.ReleaseDate, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null))
            .Where(x => x.Date is not null && scores.ContainsKey(x.Model.Id))
            .Select(x => (x.Model, Date: x.Date!.Value, Score: scores[x.Model.Id]))
            .OrderBy(x => x.Date).ThenBy(x => x.Model.Name)
            .ToList();

        var best = double.MinValue;
        var points = new List<ReleasePoint>();
        var rows = new List<ReleaseRow>();
        foreach (var x in dated)
        {
            var record = x.Score > best;
            if (record) best = x.Score;
            points.Add(new ReleasePoint(x.Date, x.Score, record));
            var id = x.Model.Id;
            rows.Add(new ReleaseRow
            {
                Date = x.Date.ToString("yyyy-MM-dd"),
                Name = x.Model.Name,
                Vendor = x.Model.Vendor ?? "",
                ScoreText = Fmt.Score(x.Score),
                IsRecord = record,
                OpenCommand = new RelayCommand(() => _state.Navigate(PageKey.Model, id.ToString())),
            });
        }

        ReleasePoints = points;
        foreach (var r in Enumerable.Reverse(rows).Take(40)) Releases.Add(r);
        if (rows.Count == 0) ReleaseNote = _loc["History_NoRelease"];
        TimelineChanged?.Invoke();
    }
}