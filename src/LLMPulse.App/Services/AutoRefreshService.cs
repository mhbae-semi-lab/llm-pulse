using System.Windows.Threading;
using LLMPulse.Core.Scheduling;

namespace LLMPulse.App.Services;

/// <summary>
/// 앱 실행 중 갱신: 시작 시 캐치업 + 1분마다 "갱신 시각이 지났는데 오늘 갱신이 없는가" 확인.
/// 같은 규칙이 절전 복귀와 정시 갱신도 처리하고, 헤드리스 작업이 갱신한 경우엔 화면만 다시 로드한다.
/// </summary>
public sealed class AutoRefreshService : IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    private readonly AppState _state;
    private readonly RefreshCoordinator _coordinator;
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private bool _checking;

    public AutoRefreshService(AppState state, RefreshCoordinator coordinator)
    {
        _state = state;
        _coordinator = coordinator;
        _timer = new DispatcherTimer { Interval = CheckEvery };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync();
    }

    public async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var last = _state.ReadLastRefresh();
            if (last != _state.LastRefreshAt) _coordinator.ReloadExternal();

            var now = DateTimeOffset.Now;
            if (now < _nextAttempt || !RefreshSchedule.IsDue(now, last, _state.RefreshTime)) return;

            var ok = await _coordinator.RunAsync(scheduled: true);
            if (!ok) _nextAttempt = now + RetryAfterFailure; // 키 없음/오프라인일 때 매분 재시도하지 않는다
        }
        finally
        {
            _checking = false;
        }
    }

    public void Dispose() => _timer.Stop();
}