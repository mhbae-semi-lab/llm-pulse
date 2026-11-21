using LLMPulse.App.Localization;

namespace LLMPulse.App;

/// <summary>"2시간 15분 후" 같은 남은 시간 문구.</summary>
public static class TimeText
{
    public static string Remaining(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t) return "";
        var loc = Loc.Instance;
        var span = t - now;
        if (span <= TimeSpan.Zero) return loc["Time_Soon"];
        if (span.TotalDays >= 1) return loc.Format("Time_DH", (int)span.TotalDays, span.Hours);
        if (span.TotalHours >= 1) return loc.Format("Time_HM", (int)span.TotalHours, span.Minutes);
        return loc.Format("Time_M", Math.Max(1, (int)Math.Ceiling(span.TotalMinutes)));
    }

    /// <summary>"초기화까지 2시간 15분" 형태. 시각을 알 수 없으면 빈 문자열.</summary>
    public static string ResetsIn(DateTimeOffset? at, DateTimeOffset now) =>
        at is null ? "" : Loc.Instance.Format("Limit_ResetsIn", Remaining(at, now));
}