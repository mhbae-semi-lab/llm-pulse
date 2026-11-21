namespace LLMPulse.Core;

/// <param name="AsOf">소스 데이터 자체의 기준일. 소스가 날짜를 주지 않으면 마지막 성공 갱신일.</param>
/// <param name="FromSource">true 면 AsOf 가 소스 데이터에 적힌 날짜 (false 면 우리가 받아 온 날짜).</param>
public sealed record SourceFreshnessInfo(string SourceId, DateOnly? AsOf, bool FromSource, int? AgeDays, bool IsStale);

/// <summary>
/// 소스 신선도. 벤치마크를 검사 데이터처럼 다룬다: 측정 날짜가 오래된 값은 표시하고, 기본으로 추천 계산에서 뺀다.
/// </summary>
public static class SourceFreshness
{
    /// <summary>이 일수를 넘게 갱신이 없으면 "오래됨".</summary>
    public const int StaleDays = 90;

    /// <summary>설정 키: 소스가 알려 준 데이터 기준일 (yyyy-MM-dd).</summary>
    public static string SettingKey(string sourceId) => $"source_as_of:{sourceId}";

    /// <summary>설정 키: "1" 이면 오래된 소스도 추천 계산에 포함.</summary>
    public const string SettingIncludeStale = "include_stale_sources";

    public static SourceFreshnessInfo Evaluate(string sourceId, DateOnly? dataAsOf, DateOnly? lastSuccess, DateOnly today)
    {
        var asOf = dataAsOf ?? lastSuccess;
        int? age = asOf is { } d ? today.DayNumber - d.DayNumber : null;
        return new SourceFreshnessInfo(sourceId, asOf, dataAsOf is not null, age, age > StaleDays);
    }

    /// <summary>"2025-10-03", "2025-10-03T09:12:38Z" 같은 문자열들 중 가장 늦은 날짜.</summary>
    public static DateOnly? Latest(IEnumerable<string?> dates)
    {
        DateOnly? best = null;
        foreach (var s in dates)
        {
            if (s is not { Length: >= 10 }) continue;
            if (DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d) && (best is null || d > best))
                best = d;
        }

        return best;
    }
}
