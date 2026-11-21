using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;

namespace LLMPulse.App.ViewModels;

public sealed partial class WizardPlanItem : ObservableObject
{
    private readonly AppState _state;
    public string Id { get; }
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";

    public bool IsSelected
    {
        get => _state.IsPlanSelected(Id);
        set { _state.SetPlanSelected(Id, value); OnPropertyChanged(); }
    }

    public WizardPlanItem(AppState state, string id) { _state = state; Id = id; }
    public void Raise() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>첫 실행 안내: API 키 → 내 구독 → 첫 갱신. 설정의 "마법사 다시 실행"으로 재진입 가능.</summary>
public sealed partial class WizardViewModel : ObservableObject
{
    public const int LastStep = 2;

    /// <summary>처음 쓰는 사용자가 고를 가능성이 높은 구독 (사용자가 직접 바꿀 수 있다).</summary>
    private static readonly string[] SuggestedPlans = ["cursor", "claude_pro", "chatgpt_plus"];

    private readonly AppState _state;
    private readonly IApiKeyStore _keys;
    private readonly RefreshCoordinator _refresher;
    private readonly Loc _loc = Loc.Instance;

    public ObservableCollection<WizardPlanItem> Plans { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _step;
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private bool _usePlanOnly = true;
    [ObservableProperty] private string _summary = "";

    public bool IsStep0 => Step == 0;
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool CanBack => Step > 0;
    public bool IsLast => Step == LastStep;
    public bool IsNotLast => Step != LastStep;
    public string StepText => _loc.Format("Wizard_Step", Step + 1, LastStep + 1);

    public IRelayCommand NextCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand SkipCommand { get; }

    public WizardViewModel(AppState state, IApiKeyStore keys, RefreshCoordinator refresher)
    {
        _state = state;
        _keys = keys;
        _refresher = refresher;
        NextCommand = new RelayCommand(Next);
        BackCommand = new RelayCommand(() => Step = Math.Max(0, Step - 1));
        SkipCommand = new RelayCommand(Close);
        _loc.LanguageChanged += () => { RebuildPlans(); UpdateKeyStatus(); UpdateSummary(); OnPropertyChanged(nameof(StepText)); };
    }

    /// <summary>처음 실행이면 마법사를 연다.</summary>
    public void OpenIfFirstRun()
    {
        if (!_state.Onboarded) Open();
    }

    public void Open()
    {
        // 구독을 아직 고르지 않은 경우에만 흔한 구독을 미리 체크해 둔다
        if (!_state.HasPlans)
            foreach (var id in SuggestedPlans)
                if (_state.Plans.Plans.Any(p => p.Id == id))
                    _state.SetPlanSelected(id, true);

#pragma warning disable MVVMTK0034 // 필드에 직접 쓰고 아래에서 알림을 한 번만 보낸다
        _usePlanOnly = true;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(UsePlanOnly));
        Step = 0;
        RebuildPlans();
        UpdateKeyStatus();
        IsOpen = true;
    }

    public void SaveApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            KeyStatus = _loc["Settings_KeyEmpty"];
            return;
        }

        _keys.Set(ArtificialAnalysisAdapter.Source, key);
        UpdateKeyStatus(saved: true);
    }

    private void UpdateKeyStatus(bool saved = false) =>
        KeyStatus = saved ? _loc["Settings_KeySaved"]
            : _keys.Has(ArtificialAnalysisAdapter.Source) ? _loc["Settings_KeyStored"] : _loc["Settings_KeyMissing"];

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep0));
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(CanBack));
        OnPropertyChanged(nameof(IsLast));
        OnPropertyChanged(nameof(IsNotLast));
        OnPropertyChanged(nameof(StepText));
        if (value == LastStep) UpdateSummary();
    }

    private void RebuildPlans()
    {
        var lang = _loc.Language;
        if (Plans.Count != _state.Plans.Plans.Count || !Plans.Select(p => p.Id).SequenceEqual(_state.Plans.Plans.Select(p => p.Id)))
        {
            Plans.Clear();
            foreach (var p in _state.Plans.Plans) Plans.Add(new WizardPlanItem(_state, p.Id));
        }

        foreach (var (item, def) in Plans.Zip(_state.Plans.Plans))
        {
            item.Title = def.Name.Get(lang);
            item.Detail = def.MonthlyUsd is { } usd
                ? (usd == 0 ? _loc["Plan_Free"] : _loc.Format("Plan_PerMonth", Fmt.Number(usd, "0.##")))
                : _loc["Plan_PerToken"];
            item.Raise();
        }
    }

    private void UpdateSummary()
    {
        var names = _state.ChosenPlans.Select(p => p.Name.Get(_loc.Language)).ToList();
        Summary = names.Count == 0 ? _loc["Wizard_NoPlans"] : string.Join(", ", names);
    }

    private void Next()
    {
        if (Step < LastStep) { Step++; return; }

        _state.PlanOnly = UsePlanOnly && _state.HasPlans;
        Close();
        // 키가 있으면 바로 첫 갱신을 시작한다
        if (_keys.Has(ArtificialAnalysisAdapter.Source) && _refresher.RefreshCommand.CanExecute(null))
            _refresher.RefreshCommand.Execute(null);
    }

    private void Close()
    {
        _state.Onboarded = true;
        IsOpen = false;
    }
}
