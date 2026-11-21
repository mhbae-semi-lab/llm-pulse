using System.Security;

namespace LLMPulse.Core.Scheduling;

/// <summary>갱신 시각 계산(캐치업 판단)과 작업 스케줄러 XML 생성. UI/OS 의존 없음.</summary>
public static class RefreshSchedule
{
    public static readonly TimeOnly DefaultTime = new(8, 0);

    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        time = DefaultTime;
        return !string.IsNullOrWhiteSpace(text)
               && TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm"],
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None, out time);
    }

    /// <summary>
    /// 지금 갱신해야 하는가: 오늘의 갱신 시각이 지났고, 마지막 갱신이 그 시각 이전이면 true.
    /// (앱 시작 캐치업, 인앱 타이머, 절전 복귀 모두 같은 규칙)
    /// </summary>
    public static bool IsDue(DateTimeOffset now, DateTimeOffset? lastRefreshAt, TimeOnly time)
    {
        var local = now.ToLocalTime();
        var todaysSlot = new DateTimeOffset(local.Date + time.ToTimeSpan(), local.Offset);
        if (local < todaysSlot) return false;
        return lastRefreshAt is null || lastRefreshAt.Value < todaysSlot;
    }

    /// <summary>
    /// schtasks /Create /XML 용 작업 정의. StartWhenAvailable 로 08:00 에 PC 가 꺼져 있었어도 켜지면 실행한다.
    /// </summary>
    public static string BuildTaskXml(string exePath, TimeOnly time, DateTime today)
    {
        var start = today.Date.Add(time.ToTimeSpan()).ToString("yyyy-MM-dd'T'HH:mm:ss");
        var exe = SecurityElement.Escape(exePath);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>LLM Pulse daily data refresh (headless)</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>--refresh</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}