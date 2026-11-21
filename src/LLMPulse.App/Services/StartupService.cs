using Microsoft.Win32;

namespace LLMPulse.App.Services;

/// <summary>
/// "Windows 시작 시 자동 실행": 현재 사용자(HKCU) Run 키에 --background 로 등록한다 (관리자 권한 불필요).
/// 사용자가 설정에서 켰을 때만 건드린다.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LLMPulse";

    public static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}