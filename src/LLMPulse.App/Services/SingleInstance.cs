namespace LLMPulse.App.Services;

/// <summary>UI 인스턴스를 하나로 제한하고, 두 번째 실행은 기존 창을 앞으로 가져온다 (트레이 상주 시 필요).</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly bool _owner;

    public bool IsFirst => _owner;

    public SingleInstance()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "user";
        _mutex = new Mutex(true, $@"Local\LLMPulse.{sid}", out _owner);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\LLMPulse.Show.{sid}");
    }

    /// <summary>첫 인스턴스가 아니면 기존 창에 신호를 보낸다.</summary>
    public void SignalExisting() => _show.Set();

    public void ListenForShow(Action onShow)
    {
        var t = new Thread(() =>
        {
            while (_show.WaitOne()) onShow();
        })
        { IsBackground = true, Name = "LLMPulse.ShowListener" };
        t.Start();
    }

    public void Dispose()
    {
        if (_owner) _mutex.ReleaseMutex();
        _mutex.Dispose(); // _show 는 리스너 스레드가 대기 중이라 프로세스 종료에 맡긴다
    }
}