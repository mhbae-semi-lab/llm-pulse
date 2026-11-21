using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;
using LLMPulse.Core;
using LLMPulse.Core.Models;
using LLMPulse.Core.Scoring;

namespace LLMPulse.App.ViewModels;

public sealed partial class CategoryCardViewModel : ObservableObject
{
    public required string CategoryId { get; init; }
    public required string Title { get; init; }
    public required string InfoMetricId { get; init; }
    public bool HasScore { get; init; }
    public string BestLabel { get; init; } = "";
    public string BestName { get; init; } = "";
    public string BestMeta { get; init; } = "";
    public bool HasValue { get; init; }
    public string ValueName { get; init; } = "";
    public string ValueMeta { get; init; } = "";
    public bool HasPlan { get; init; }
    public string PlanName { get; init; } = "";
    public string PlanMeta { get; init; } = "";
    public string Rationale { get; init; } = "";
    public string Badge { get; init; } = "";
    public int Index { get; init; }
    public int? RankDelta { get; init; }
    public bool HasDelta => RankDelta is not null;
    public string DeltaText => RankDelta switch { > 0 => $"▲{RankDelta}", < 0 => $"▼{-RankDelta}", 0 => "–", _ => "" };
    public bool DeltaUp => RankDelta > 0;
    public bool DeltaDown => RankDelta < 0;
    public IReadOnlyList<double> Trend { get; init; } = [];
    public bool HasTrend => Trend.Count >= 2;
    public bool HasBadge => Badge.Length > 0;
    /// <summary>카드 아래 한 줄: 1위가 바뀐 이유, 오래된 소스 제외 안내와 Shadow(포함 시 1위).</summary>
    public string Note { get; init; } = "";
    public bool HasNote => Note.Length > 0;

    public required IRelayCommand OpenCommand { get; init; }
    public required IRelayCommand InfoCommand { get; init; }
}

public sealed partial class PurposeChip : ObservableObject
{
    public required string Key { get; init; }
    public required string TitleKey { get; init; }
    public required string CategoryId { get; init; }
    public bool Cheapest { get; init; }
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isSelected;
    public IRelayCommand SelectCommand { get; set; } = null!;
}

public sealed class PinnedModel
{
    public required long ModelId { get; init; }
    public required string Name { get; init; }
    public required string Meta { get; init; }
    public required IRelayCommand OpenCommand { get; init; }
}

public sealed class LimitBarItem
{
    public required string Title { get; init; }
    public required string Text { get; init; }
    public double Ratio { get; init; }
    /// <summary>ok | warn | full</summary>
    public string Level { get; init; } = "ok";
    public required IRelayCommand AddCommand { get; init; }
}

public sealed class PickAlternative
{
    public required string Name { get; init; }
    public required string Meta { get; init; }
    public IRelayCommand? OpenCommand { get; init; }
}

public sealed partial class DashboardViewModel : ObservableObject
{
    public const string PickCategoryId = "my_coding";
    public const string SettingPurpose = "dash_purpose";

    public ObservableCollection<PurposeChip> Purposes { get; } = [];
    private PurposeChip _purpose = null!;
    public bool ShowPlanColumn => _state.HasPlans && !_state.Restricting;

    private readonly AppState _state;
    private readonly Catalog _catalog;
    private readonly Loc _loc = Loc.Instance;

    public RefreshCoordinator Refresher { get; }
    public ObservableCollection<CategoryCardViewModel> Cards { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasData;

    [ObservableProperty] private bool _hasPick;
    [ObservableProperty] private string _pickTitle = "";
    [ObservableProperty] private string _pickName = "";
    private long _pickModelId;
    public IRelayCommand OpenPickModelCommand => new RelayCommand(() => { if (_pickModelId != 0) _state.Navigate(PageKey.Model, _pickModelId.ToString()); });
    [ObservableProperty] private string _pickMeta = "";
    [ObservableProperty] private string _pickWhy = "";
    [ObservableProperty] private bool _pickLimited;
    public ObservableCollection<PickAlternative> Alternatives { get; } = [];
    public ObservableCollection<LimitBarItem> LimitBars { get; } = [];
    public bool HasLimitBars => LimitBars.Count > 0;
    public bool HasAlternatives => Alternatives.Count > 0;
    public IRelayCommand OpenPickCommand { get; }

    public ObservableCollection<string> Changes { get; } = [];
    [ObservableProperty] private string _changesHeader = "";
    [ObservableProperty] private string _changesEmpty = "";
    [ObservableProperty] private bool _hasChangesHeader;
    public bool HasChanges => Changes.Count > 0;

    [ObservableProperty] private string _limitAlert = "";
    [ObservableProperty] private bool _hasLimitAlert;

    // 즐겨찾기 모델
    public ObservableCollection<PinnedModel> Pinned { get; } = [];
    public bool HasPinned => Pinned.Count > 0;

    public DashboardViewModel(AppState state, Catalog catalog, RefreshCoordinator refresher)
    {
        _state = state;
        _catalog = catalog;
        Refresher = refresher;
        foreach (var (key, titleKey, cat, cheap) in new[]
        {
            ("coding", "Purpose_Coding", "my_coding", false), ("korean", "Purpose_Korean", "korean", false),
            ("fast", "Purpose_Fast", "speed", false), ("balanced", "Purpose_Balanced", "overall", false),
            ("cheap", "Purpose_Cheap", "my_coding", true),
        })
        {
            var chip = new PurposeChip { Key = key, TitleKey = titleKey, CategoryId = cat, Cheapest = cheap };
            chip.SelectCommand = new RelayCommand(() => SelectPurpose(chip));
            Purposes.Add(chip);
        }
        OpenPickCommand = new RelayCommand(() => state.Navigate(PageKey.Category, _purpose.CategoryId));
        state.DataChanged += Rebuild;
        state.LimitsChanged += BuildLimitAlert;
        var savedPurpose = state.ReadSetting(SettingPurpose);
        _purpose = Purposes.FirstOrDefault(p => p.Key == savedPurpose) ?? Purposes[0];
        _purpose.IsSelected = true;
        state.FavoritesChanged += () => { if (_state.Snapshot is { } s) BuildPinned(s); };
        _loc.LanguageChanged += Rebuild;
        Rebuild();
    }

    private void BuildLimitAlert()
    {
        var lang = _loc.Language;
        var now = DateTimeOffset.UtcNow;
        var lines = new List<string>();
        LimitBars.Clear();
        foreach (var info in _state.LimitInfos())
        {
            var r = info.Ratio;
            var name = info.Plan.Name.Get(lang);
            var reset = TimeText.ResetsIn(info.ResetAt, now);
            if (r >= 0.8)
            {
                var line = _loc.Format(r >= 1 ? "Limit_Reached" : "Limit_Warn", name,
                    info.Used.ToString("N0", _loc.Culture), info.Cap.ToString("N0", _loc.Culture),
                    (r * 100).ToString("0", _loc.Culture));
                lines.Add(reset.Length > 0 ? $"{line} ({reset})" : line);
            }

            var planId = info.Plan.Id;
            LimitBars.Add(new LimitBarItem
            {
                Title = name,
                Text = $"{info.Used.ToString("N0", _loc.Culture)} / {info.Cap.ToString("N0", _loc.Culture)}"
                       + (reset.Length > 0 ? $" · {reset}" : ""),
                Ratio = Math.Min(1, r),
                Level = r >= 1 ? "full" : r >= 0.8 ? "warn" : "ok",
                AddCommand = new RelayCommand(() => _state.AddUsage(planId, +1)),
            });
        }

        OnPropertyChanged(nameof(HasLimitBars));
        LimitAlert = string.Join(Environment.NewLine, lines);
        HasLimitAlert = lines.Count > 0;
    }
    private void Rebuild()
    {
        foreach (var p in Purposes) p.Title = _loc[p.TitleKey];
        OnPropertyChanged(nameof(ShowPlanColumn));
        BuildLimitAlert();
        Cards.Clear();
        HasData = _state.HasData;
        var snap = _state.Snapshot;
        if (snap is null || !HasData)
        {
            Summary = "";
            HasPick = false;
            HasChangesHeader = false;
            return;
        }

        Summary = _loc.Format("Dashboard_Summary",
            snap.NewModelCount.ToString("N0", _loc.Culture),
            snap.TopChangeCount.ToString("N0", _loc.Culture));

        var index = 0;
        foreach (var cr in snap.Categories)
            Cards.Add(BuildCard(cr, snap, index++));

        BuildPick(snap);
        BuildPinned(snap);
        BuildChanges(snap);
    }

    private void SelectPurpose(PurposeChip chip)
    {
        if (chip == _purpose) return;
        foreach (var p in Purposes) p.IsSelected = p == chip;
        _purpose = chip;
        _state.SaveSetting(SettingPurpose, chip.Key);
        if (_state.Snapshot is { } s) BuildPick(s);
    }

    private void BuildPick(ScoringSnapshot snap)
    {
        foreach (var p in Purposes) p.Title = _loc[p.TitleKey];
        Alternatives.Clear();
        HasPick = false;
        var cr = snap.Categories.FirstOrDefault(c => c.Category.Id == _purpose.CategoryId);
        if (cr is null) { OnPropertyChanged(nameof(HasAlternatives)); return; }

        Func<long, bool>? allowed = _state.HasPlans ? _state.InPlan : null;
        var top = Recommender.TopN(cr.Scores, snap.Models, _purpose.Cheapest ? 40 : 3, allowed);
        if (_purpose.Cheapest && top.Count > 0)
        {
            var floor = (top[0].Score ?? 0) * _state.Tolerance;
            top = top.Where(s => (s.Score ?? 0) >= floor && snap.Models[s.ModelId].BlendedPrice is not null)
                .OrderBy(s => snap.Models[s.ModelId].BlendedPrice).Take(3).ToList();
        }

        if (top.Count == 0) { OnPropertyChanged(nameof(HasAlternatives)); return; }

        var lang = _loc.Language;
        string Meta(CategoryScore s)
        {
            var m = snap.Models[s.ModelId];
            var text = $"{_loc["Card_Score"]} {Fmt.Score(s.Score)} · {Fmt.Price(m.BlendedPrice)}/1M";
            return _state.HasPlans && _state.AccessOf(m) == LLMPulse.Core.Plans.PlanAccess.Limited
                ? text + " · ⚠ " + _loc["Plans_AccessLimited"] : text;
        }

        var best = top[0];
        PickTitle = _loc.Format(_state.HasPlans ? "Pick_TitleForPlan" : "Pick_TitleFor", _purpose.Title);
        PickName = snap.Models[best.ModelId].Name;
        _pickModelId = best.ModelId;
        PickMeta = Meta(best);
        PickLimited = _state.HasPlans && _state.AccessOf(best.ModelId) == LLMPulse.Core.Plans.PlanAccess.Limited;

        var parts = CategoryScorer.Breakdown(cr.Category, _catalog.MetricsById, snap.Models.Values.ToList(), best.ModelId)
            .Take(3)
            .Select(c => $"{(_catalog.MetricsById.TryGetValue(c.MetricId, out var d) ? d.Name.Get(lang) : c.MetricId)} {Fmt.Score(c.Score)} ({Fmt.Percent(c.WeightShare, "0%")})");
        PickWhy = _loc.Format("Pick_Why", string.Join(" · ", parts));

        foreach (var alt in top.Skip(1))
            Alternatives.Add(new PickAlternative
            {
                Name = snap.Models[alt.ModelId].Name, Meta = Meta(alt),
                OpenCommand = new RelayCommand(() => _state.Navigate(PageKey.Model, alt.ModelId.ToString())),
            });
        OnPropertyChanged(nameof(HasAlternatives));
        HasPick = true;
    }

    private void BuildChanges(ScoringSnapshot snap)
    {
        Changes.Clear();
        var lang = _loc.Language;
        var catName = _state.Snapshot!.Categories.ToDictionary(c => c.Category.Id, c => c.Category.Name.Get(lang));
        string Cat(string id) => catName.GetValueOrDefault(id, id);

        var items = _state.Changes.Where(c => !_state.Restricting || _state.InPlan(c.ModelId)).Take(4);
        foreach (var c in items)
        {
            if (!snap.Models.TryGetValue(c.ModelId, out var m)) continue;
            var other = c.OtherModelId is { } o && snap.Models.TryGetValue(o, out var om) ? om.Name : "";
            Changes.Add(c.Kind switch
            {
                ChangeKind.NewModel when c.ToRank is { } r => _loc.Format("Change_NewRanked", m.Name, Cat(c.CategoryId), r),
                ChangeKind.NewModel => _loc.Format("Change_New", m.Name),
                ChangeKind.TopChanged => _loc.Format("Change_Top", m.Name, Cat(c.CategoryId), other)
                    + (snap.Categories.FirstOrDefault(x => x.Category.Id == c.CategoryId) is { TopChangeReason: not null } tc
                        ? " — " + ReasonText(tc, snap) : ""),
                ChangeKind.EnteredTop => _loc.Format("Change_Entered", m.Name, Cat(c.CategoryId), c.ToRank ?? 0),
                ChangeKind.RankUp => _loc.Format("Change_Up", m.Name, Cat(c.CategoryId), c.FromRank ?? 0, c.ToRank ?? 0),
                _ => _loc.Format("Change_Down", m.Name, Cat(c.CategoryId), c.FromRank ?? 0, c.ToRank ?? 0),
            });
        }

        foreach (var pc in _state.PriceChanges.Where(p => !_state.Restricting || _state.InPlan(p.ModelId)).Take(2))
        {
            if (!snap.Models.TryGetValue(pc.ModelId, out var pm)) continue;
            Changes.Add(_loc.Format(pc.Pct < 0 ? "Change_PriceDown" : "Change_PriceUp",
                pm.Name, Fmt.Price(pc.Old), Fmt.Price(pc.New), Math.Abs(pc.Pct).ToString("0", _loc.Culture)));
        }

        HasChangesHeader = _state.ChangesSince is not null || Changes.Count > 0;
        ChangesHeader = _state.ChangesSince is { } since ? _loc.Format("Changes_Since", since) : "";
        ChangesEmpty = Changes.Count == 0 && HasChangesHeader ? _loc["Changes_Empty"] : "";
        OnPropertyChanged(nameof(HasChanges));
    }

    private void BuildPinned(ScoringSnapshot snap)
    {
        Pinned.Clear();
        var cr = snap.Categories.FirstOrDefault(c => c.Category.Id == PickCategoryId);
        if (cr is not null)
        {
            var byId = cr.Scores.ToDictionary(s => s.ModelId);
            foreach (var id in _state.Favorites
                         .Where(snap.Models.ContainsKey)
                         .OrderByDescending(id => byId.TryGetValue(id, out var s) ? s.Score ?? -1 : -1))
            {
                var m = snap.Models[id];
                var s = byId.GetValueOrDefault(id);
                var parts = new List<string>();
                if (s?.Score is { } sc) parts.Add($"{_loc["Card_Score"]} {Fmt.Score(sc)}");
                if (s?.Rank is { } r) parts.Add($"#{r}");
                parts.Add($"{Fmt.Price(m.BlendedPrice)}/1M");
                if (_state.HasPlans && _state.AccessOf(m) == LLMPulse.Core.Plans.PlanAccess.None) parts.Add(_loc["Plans_AccessNone"]);
                Pinned.Add(new PinnedModel
                {
                    ModelId = id, Name = m.Name, Meta = string.Join(" · ", parts),
                    OpenCommand = new RelayCommand(() => _state.Navigate(PageKey.Model, id.ToString())),
                });
            }
        }

        OnPropertyChanged(nameof(HasPinned));
    }

    private CategoryCardViewModel BuildCard(CategoryResult cr, ScoringSnapshot snap, int index)
    {
        var lang = _loc.Language;
        var cat = cr.Category;
        var rec = cr.Recommendation;
        var primaryMetricId = cat.Metrics.First().Id;
        _catalog.MetricsById.TryGetValue(primaryMetricId, out var primaryMetric);

        string bestName = "", bestMeta = "", valueName = "", valueMeta = "", rationale = "";
        var hasValue = false;

        if (rec.Best is { } best)
        {
            var m = snap.Models[best.ModelId];
            bestName = m.Name;
            if (_state.Restricting && _state.AccessOf(m) == LLMPulse.Core.Plans.PlanAccess.Limited) bestName += " ⚠";
            var scored = cr.Scores.Count(s => s.Score is not null);

            if (cat.ValueMode)
            {
                bestMeta = $"{_loc["Card_Score"]} {Fmt.Score(best.Score)} · {Fmt.Price(m.BlendedPrice)}/1M";
                rationale = _loc.Format("Rationale_Best", cat.Name.Get(lang), Fmt.Score(best.Score), scored.ToString("N0", _loc.Culture));
            }
            else
            {
                CategoryScorer.TryGetRaw(m, primaryMetricId, out var raw);
                bestMeta = primaryMetricId == MetricKeys.BlendedPrice
                    ? $"{Fmt.Price(raw)}/1M"
                    : $"{FormatRaw(primaryMetricId, raw)} {primaryMetric?.Unit}".Trim();
                rationale = _loc.Format("Rationale_BestRaw", cat.Name.Get(lang), bestMeta, scored.ToString("N0", _loc.Culture));
            }

            if (rec.BestValue is { } val)
            {
                var vm = snap.Models[val.ModelId];
                hasValue = true;
                valueName = vm.Name;
                valueMeta = $"{_loc["Card_Score"]} {Fmt.Score(val.Score)} · {Fmt.Price(rec.BestValuePrice)}/1M";
                var ratio = best.Score > 0 ? val.Score!.Value / best.Score!.Value : 1;
                rationale = _loc.Format("Rationale_Value",
                    cat.Name.Get(lang),
                    Fmt.Score(val.Score),
                    Fmt.Percent(ratio),
                    Fmt.Score(best.Score),
                    Fmt.Price(rec.BestValuePrice),
                    Fmt.Percent(rec.Tolerance, "0.#%"));
            }
        }

        var hasPlan = false;
        string planName = "", planMeta = "";
        if (_state.HasPlans && !_state.Restricting)
        {
            hasPlan = cr.PlanBest is not null;
            if (cr.PlanBest is { } pb)
            {
                var pm = snap.Models[pb.ModelId];
                planName = pm.Name;
                if (cr.PlanBestAccess == LLMPulse.Core.Plans.PlanAccess.Limited) planName += " ⚠";
                if (cat.ValueMode) planMeta = $"{_loc["Card_Score"]} {Fmt.Score(pb.Score)}";
                else
                {
                    CategoryScorer.TryGetRaw(pm, primaryMetricId, out var praw);
                    planMeta = primaryMetricId == MetricKeys.BlendedPrice
                        ? $"{Fmt.Price(praw)}/1M"
                        : $"{FormatRaw(primaryMetricId, praw)} {primaryMetric?.Unit}".Trim();
                }
            }
        }

        int? delta = null;
        IReadOnlyList<double> trend = [];
        if (rec.Best is { } bestScore)
        {
            if (_state.PreviousRanks.TryGetValue(cat.Id, out var prevRanks)
                && prevRanks.TryGetValue(bestScore.ModelId, out var prevRank)
                && cr.Scores.FirstOrDefault(s => s.ModelId == bestScore.ModelId)?.Rank is { } curRank)
                delta = prevRank - curRank;
            trend = _state.ScoreTrend(cat.Id, bestScore.ModelId);
        }

        var badge = "";
        var notes = new List<string>();
        if (cr.TopChanged && cr.PreviousTopModelId is { } prev && snap.Models.TryGetValue(prev, out var prevModel))
        {
            badge = _loc.Format("Badge_TopChanged", prevModel.Name);
            notes.Add(cr.TopChangeReason is null ? badge : $"{badge}: {ReasonText(cr, snap)}");
        }

        if (StaleNote(cr, snap) is { Length: > 0 } stale) notes.Add(stale);

        return new CategoryCardViewModel
        {
            CategoryId = cat.Id,
            Title = cat.Name.Get(lang),
            InfoMetricId = primaryMetricId,
            HasScore = rec.Best is not null,
            BestLabel = cat.ValueMode ? _loc["Card_Best"] : _loc["Card_Top"],
            BestName = bestName,
            BestMeta = bestMeta,
            HasValue = hasValue,
            ValueName = valueName,
            ValueMeta = valueMeta,
            HasPlan = hasPlan,
            PlanName = planName,
            PlanMeta = planMeta,
            Rationale = rationale,
            Badge = badge,
            Note = string.Join("  ·  ", notes),
            Index = index,
            RankDelta = delta,
            Trend = trend,
            OpenCommand = new RelayCommand(() => _state.Navigate(PageKey.Category, cat.Id)),
            InfoCommand = new RelayCommand(() => _state.Navigate(PageKey.Metrics, primaryMetricId)),
        };
    }

    /// <summary>1위가 바뀐 이유 한 줄 (TopChangeExplainer 결과를 화면 문장으로).</summary>
    private string ReasonText(CategoryResult cr, ScoringSnapshot snap)
    {
        if (cr.TopChangeReason is not { } r) return "";
        string Name(long? id) => id is { } i && snap.Models.TryGetValue(i, out var m) ? m.Name : Fmt.Dash;
        string Val(double? v, string metric) => v is { } x ? FormatRaw(metric, x) : Fmt.Dash;
        var oldName = Name(cr.PreviousTopModelId);
        return r.Cause switch
        {
            TopChangeCause.NewEntry => _loc["Reason_NewEntry"],
            TopChangeCause.PreviousUnscored => _loc.Format("Reason_PreviousUnscored", oldName),
            TopChangeCause.RuleChanged => _loc["Reason_RuleChanged"],
            _ => _loc.Format("Reason_MetricShift",
                _catalog.MetricsById.TryGetValue(r.MetricId!, out var md) ? md.Name.Get(_loc.Language) : r.MetricId!,
                Name(cr.Recommendation.Best?.ModelId), Val(r.NewBefore, r.MetricId!), Val(r.NewAfter, r.MetricId!),
                oldName, Val(r.OldBefore, r.MetricId!), Val(r.OldAfter, r.MetricId!)),
        };
    }

    /// <summary>오래된 소스를 뺀 분야: 무엇을 뺐는지 + Shadow(포함했다면 1위).</summary>
    private string StaleNote(CategoryResult cr, ScoringSnapshot snap)
    {
        if (cr.ExcludedMetrics.Count == 0) return "";
        var sources = string.Join(", ", cr.ExcludedMetrics
            .Select(id => _catalog.MetricsById.TryGetValue(id, out var md) ? md.Source : id).Distinct());
        var text = _loc.Format("Stale_Excluded", sources, LLMPulse.Core.SourceFreshness.StaleDays);
        if (cr.ShadowBest is { } sh && snap.Models.TryGetValue(sh.ModelId, out var sm))
            text += " " + (sh.ModelId == cr.Recommendation.Best?.ModelId
                ? _loc["Stale_ShadowSame"]
                : _loc.Format("Stale_Shadow", sm.Name));
        return text;
    }

    private static string FormatRaw(string metricId, double v) => metricId switch
    {
        MetricKeys.BlendedPrice => Fmt.Price(v),
        "ttft_s" => Fmt.Number(v, "0.00"),
        _ => Fmt.Number(v, "0.#"),
    };
}
