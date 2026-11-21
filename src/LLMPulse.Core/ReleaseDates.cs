namespace LLMPulse.Core;

public static class ReleaseDates
{
    /// <summary>출시일("yyyy-MM-dd")이 기준일(UTC)로부터 days 일 이내인가. 미래 날짜/파싱 불가는 false.</summary>
    public static bool IsRecent(string? release, DateOnly todayUtc, int days) =>
        DateOnly.TryParseExact(release, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d)
        && d <= todayUtc && todayUtc.DayNumber - d.DayNumber <= days;
}