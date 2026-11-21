using System.IO;
using System.Windows;
using LLMPulse.Core.Storage;

namespace LLMPulse.App.Services;

/// <summary>처리되지 않은 예외를 파일로 남기고, 사용자에게는 조용히 사라지는 대신 안내를 보여준다.</summary>
public static class CrashLog
{
    public static string Path => System.IO.Path.Combine(AppPaths.DataDirectory, "crash.log");

    public static void Hook(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write("ui", e.Exception);
            e.Handled = true; // 화면 하나의 오류로 앱 전체가 죽지 않게 한다
            Show(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("domain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("task", e.Exception); e.SetObserved(); };
    }

    public static void Write(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(Path, $"[{DateTimeOffset.Now:O}] {kind}\n{ex}\n\n");
        }
        catch { /* 로그 실패는 무시 */ }
    }

    public static void Show(Exception ex)
    {
        try
        {
            MessageBox.Show(
                $"{Localization.Loc.Instance["Crash_Body"]}\n\n{ex.Message}\n\n{Path}",
                "LLM Pulse", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* UI 불가 상황 */ }
    }
}