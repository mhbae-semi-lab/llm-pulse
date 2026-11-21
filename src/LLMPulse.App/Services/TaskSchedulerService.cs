using System.Diagnostics;
using LLMPulse.Core.Scheduling;

namespace LLMPulse.App.Services;

/// <summary>Windows 작업 스케줄러 등록/해제 (schtasks.exe + 임시 XML; .bat 파일은 만들지 않는다).</summary>
public sealed class TaskSchedulerService
{
    public const string TaskName = "LLMPulse Daily Refresh";

    public bool IsRegistered() => Run("/Query", "/TN", TaskName).ExitCode == 0;

    public (bool Ok, string Message) Register(TimeOnly time)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return (false, "Cannot determine executable path.");

        var xmlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"llmpulse-task-{Guid.NewGuid():N}.xml");
        try
        {
            // 작업 스케줄러 XML 은 UTF-16(BOM) 이어야 한다
            System.IO.File.WriteAllText(xmlPath, RefreshSchedule.BuildTaskXml(exe, time, DateTime.Today), System.Text.Encoding.Unicode);
            var r = Run("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
            return (r.ExitCode == 0, r.Output);
        }
        finally
        {
            try { System.IO.File.Delete(xmlPath); } catch { /* best effort */ }
        }
    }

    public (bool Ok, string Message) Unregister()
    {
        if (!IsRegistered()) return (true, "");
        var r = Run("/Delete", "/TN", TaskName, "/F");
        return (r.ExitCode == 0, r.Output);
    }

    private static (int ExitCode, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, output.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}