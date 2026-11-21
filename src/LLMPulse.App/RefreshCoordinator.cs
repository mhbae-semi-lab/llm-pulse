using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLMPulse.App.Localization;
using LLMPulse.Core;

namespace LLMPulse.App;

/// <summary>수동 새로고침 (최소 간격 10분으로 API 한도 보호) + 예약 갱신 + 상태 메시지.</summary>
public sealed partial class RefreshCoordinator : ObservableObject
{
    private readonly RefreshService _refresh;
    private readonly AppState _state;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "";

    /// <summary>갱신(또는 외부 프로세스의 갱신 감지) 후 상태가 다시 로드됐다. summary 가 null 이면 외부 갱신.</summary>
    public event Action<RefreshSummary?>? Refreshed;

    public RefreshCoordinator(RefreshService refresh, AppState state)
    {
        _refresh = refresh;
        _state = state;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunAsync(scheduled: false);

    /// <returns>하나 이상의 소스가 성공하면 true.</returns>
    public async Task<bool> RunAsync(bool scheduled)
    {
        var loc = Loc.Instance;
        if (IsBusy) return false;

        // 수동 새로고침만 쿨다운 적용 (예약 갱신은 하루 한 번이라 한도에 영향 없음)
        if (!scheduled && _state.LastRefreshAt is { } last)
        {
            var remaining = AppState.ManualRefreshMinInterval - (DateTimeOffset.UtcNow - last);
            if (remaining > TimeSpan.Zero)
            {
                StatusText = loc.Format("Status_Cooldown", Math.Ceiling(remaining.TotalMinutes).ToString("0", loc.Culture));
                return false;
            }
        }

        IsBusy = true;
        StatusText = loc["Status_Refreshing"];
        if (!scheduled) LLMPulse.Core.Sources.ResponseCache.TtlOverride = TimeSpan.FromHours(1);
        try
        {
            var summary = await Task.Run(() => _refresh.RefreshAsync()).ConfigureAwait(true);
            _state.Reload();

            var firstError = summary.Sources.FirstOrDefault(s => s.Error is not null);
            StatusText = firstError is null
                ? loc.Format("Status_RefreshOk", summary.TotalModels.ToString("N0", loc.Culture))
                : loc.Format("Status_RefreshError", firstError.SourceId, firstError.Error ?? "");
            Refreshed?.Invoke(summary);
            return summary.PrimaryOk;
        }
        catch (Exception ex)
        {
            StatusText = loc.Format("Status_RefreshError", "-", ex.Message);
            return false;
        }
        finally
        {
            LLMPulse.Core.Sources.ResponseCache.TtlOverride = null;
            IsBusy = false;
        }
    }

    /// <summary>헤드리스 예약 작업이 DB 를 갱신했을 때 화면을 다시 로드한다.</summary>
    public void ReloadExternal()
    {
        _state.Reload();
        Refreshed?.Invoke(null);
    }

    private bool CanRefresh() => !IsBusy;
}