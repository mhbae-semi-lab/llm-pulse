using LLMPulse.App.Services;
using Velopack;

namespace LLMPulse.App;

/// <summary>
/// 진입점. Velopack 설치 · 업데이트 · 제거 훅이 WPF 보다 먼저 돌아야 해서 App.xaml 의 자동 Main 대신 이것을 쓴다 (csproj StartupObject).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            // 제거 전에 이 앱이 등록한 자동 실행 · 예약 작업을 지운다 (사용자 데이터 %LOCALAPPDATA%\LLMPulse 는 남긴다)
            .OnBeforeUninstallFastCallback(_ =>
            {
                try { StartupService.Set(false); } catch (Exception ex) { CrashLog.Write("uninstall-startup", ex); }
                try { new TaskSchedulerService().Unregister(); } catch (Exception ex) { CrashLog.Write("uninstall-task", ex); }
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
