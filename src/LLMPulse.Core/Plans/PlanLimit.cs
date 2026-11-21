namespace LLMPulse.Core.Plans;

/// <summary>구독 사용량 한도 메모. 공개 API 가 없으므로 사용자가 정한 상한(cap)과 직접 센 횟수(used).</summary>
public sealed record PlanLimit(string PlanId, int? Cap, string Period, int Used, DateTimeOffset? PeriodStart, string Note)
{
    public static readonly string[] Periods = ["5h", "day", "week", "month"];

    /// <summary>기간이 지났으면 횟수를 0 으로 되돌린 상태를 돌려준다. (5h 는 첫 사용 시점부터의 고정 창)</summary>
    public PlanLimit Effective(DateTimeOffset now) =>
        IsExpired(Period, PeriodStart, now) ? this with { Used = 0, PeriodStart = null } : this;

    public static bool IsExpired(string period, DateTimeOffset? start, DateTimeOffset now)
    {
        if (start is null) return false;
        var s = start.Value.ToLocalTime();
        var n = now.ToLocalTime();
        return period switch
        {
            "5h" => n - s >= TimeSpan.FromHours(5),
            "day" => s.Date < n.Date,
            "week" => MondayOf(s.Date) < MondayOf(n.Date),
            "month" => (s.Year, s.Month) != (n.Year, n.Month),
            _ => false,
        };
    }

    /// <summary>
    /// 다음 초기화 시각. 5h 는 첫 사용 시점부터의 고정 창이라 사용 전에는 null,
    /// day/week/month 는 달력 경계(자정, 월요일, 1일 — 로컬 시간).
    /// </summary>
    public DateTimeOffset? ResetAt(DateTimeOffset now)
    {
        var n = now.ToLocalTime();
        switch (Period)
        {
            case "5h":
                return PeriodStart is { } s && Used > 0 ? s + TimeSpan.FromHours(5) : null;
            case "day":
                return new DateTimeOffset(n.Date.AddDays(1));
            case "week":
                return new DateTimeOffset(MondayOf(n.Date).AddDays(7));
            case "month":
                return new DateTimeOffset(new DateTime(n.Year, n.Month, 1).AddMonths(1));
            default:
                return null;
        }
    }

    /// <summary>상한 대비 사용 비율 (상한 없으면 null).</summary>
    public double? Ratio => Cap is > 0 ? Math.Min(1.0, (double)Used / Cap.Value) : null;

    private static DateTime MondayOf(DateTime d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
}