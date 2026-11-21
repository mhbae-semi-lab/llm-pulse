using System.Text.RegularExpressions;

namespace LLMPulse.Core;

/// <summary>최근 출시됐는데 어떤 분야에서 점수가 아직 없는 모델 찾기 (소스 데이터 반영이 늦은 경우를 사용자에게 알리기 위함).</summary>
public static partial class CoverageGaps
{
    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex TrailingParen();

    /// <summary>"Claude Fable 5.2 (High, ...)" → "Claude Fable 5.2" : 추론 강도 변종은 한 모델로 센다.</summary>
    public static string BaseName(string name) => TrailingParen().Replace(name, "").Trim();

    /// <summary>
    /// 최근(days일 이내, UTC) 출시 변종이 있는데 어느 변종도 점수가 없는 모델의 기본 이름 목록 (이름순).
    /// </summary>
    public static IReadOnlyList<string> RecentUnscored(
        IEnumerable<(long Id, string Name, string? Release)> models,
        IReadOnlySet<long> scoredIds, DateOnly todayUtc, int days) =>
        models
            .GroupBy(m => BaseName(m.Name), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Any(m => ReleaseDates.IsRecent(m.Release, todayUtc, days)) && !g.Any(m => scoredIds.Contains(m.Id)))
            .Select(g => g.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}