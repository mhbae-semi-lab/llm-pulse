using System.Text.RegularExpressions;

namespace LLMPulse.Core.Matching;

/// <summary>
/// 소스마다 다른 모델명을 같은 모델로 묶기 위한 보수적 이름 매칭.
/// 틀린 자동 매칭이 추천 신뢰도를 해치므로, 후보가 둘 이상이면 매칭하지 않고 사용자에게 맡긴다.
/// </summary>
public static partial class AliasResolver
{
    [GeneratedRegex(@"\(([^)]*)\)")]
    private static partial Regex Parenthetical();

    [GeneratedRegex(@"(?<=\d)[-.](?=\d)")]
    private static partial Regex DigitSeparator();

    [GeneratedRegex(@"[a-z]+|\d+(?:\.\d+)?")]
    private static partial Regex TokenPattern();

    /// <summary>"Provider: Name" 접두를 제거하고 소문자 토큰 집합으로 만든다 (순서 무관, 4-5 == 4.5).</summary>
    public static string[] Tokens(string name, bool includeParenthetical)
    {
        var s = name;
        var colon = s.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0) s = s[(colon + 2)..];

        s = includeParenthetical ? s : Parenthetical().Replace(s, " ");
        s = DigitSeparator().Replace(s.ToLowerInvariant(), ".");
        return TokenPattern().Matches(s).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal).ToArray();
    }

    public static string Key(string name, bool includeParenthetical) =>
        string.Join(' ', Tokens(name, includeParenthetical));

    /// <summary>
    /// 1순위: 괄호 포함 토큰 집합이 유일하게 일치. 2순위: 괄호 제외 토큰 집합이 유일하게 일치.
    /// 둘 다 모호하거나 없으면 null.
    /// </summary>
    public static long? FindUnique(string name, IReadOnlyList<(long Id, string Name)> candidates)
    {
        foreach (var withParens in new[] { true, false })
        {
            var key = Key(name, withParens);
            if (key.Length == 0) continue;
            var hits = candidates.Where(c => Key(c.Name, withParens) == key).Select(c => c.Id).Distinct().ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) return null;
        }

        return null;
    }

    /// <summary>
    /// 유일 일치가 없을 때의 보수적 자동 매칭: 버전 숫자 토큰이 완전히 같고, 괄호 포함 자카드 유사도가
    /// 0.8 이상이며 2위와 0.1 이상 차이날 때만 인정한다. 동점/애매하면 null (수동 매칭에 맡김).
    /// </summary>
    public static long? FindConfident(string name, IReadOnlyList<(long Id, string Name)> candidates)
    {
        var a = Tokens(name, true).ToHashSet();
        if (a.Count == 0) return null;
        var nums = a.Where(t => char.IsDigit(t[0])).Order(StringComparer.Ordinal).ToArray();

        var scored = candidates
            .Select(c =>
            {
                var b = Tokens(c.Name, true).ToHashSet();
                var bn = b.Where(t => char.IsDigit(t[0])).Order(StringComparer.Ordinal).ToArray();
                var inter = a.Count(b.Contains);
                var union = a.Count + b.Count - inter;
                return (c.Id, Score: nums.SequenceEqual(bn) && union > 0 ? (double)inter / union : 0);
            })
            .Where(x => x.Score > 0)
            .GroupBy(x => x.Id).Select(g => g.First())
            .OrderByDescending(x => x.Score)
            .Take(2)
            .ToList();

        if (scored.Count == 0 || scored[0].Score < 0.8) return null;
        if (scored.Count > 1 && scored[0].Score - scored[1].Score < 0.1) return null;
        return scored[0].Id;
    }

    /// <summary>
    /// 소스 이름에 추론 강도 표기가 없는데(예: OpenRouter "Claude Fable 5.2") 후보가 "(Low)/(Medium)/(High)…" 변종으로 갈라져 있으면
    /// 기본 강도인 Medium 변종을 고른다. Medium 이 정확히 하나일 때만 인정하고, 아니면 null.
    /// </summary>
    public static long? FindDefaultVariant(string name, IReadOnlyList<(long Id, string Name)> candidates)
    {
        if (Parenthetical().IsMatch(name)) return null; // 이미 강도가 적혀 있으면 FindUnique 몫
        var key = Key(name, false);
        if (key.Length == 0) return null;

        var mediums = candidates
            .Where(c => Key(c.Name, false) == key
                && Parenthetical().Match(c.Name) is { Success: true } p
                && TokenPattern().Match(p.Groups[1].Value.ToLowerInvariant()) is { Success: true, Value: "medium" })
            .Select(c => c.Id).Distinct().ToList();
        return mediums.Count == 1 ? mediums[0] : null;
    }

    /// <summary>유일 일치 → 기본 변종(Medium) → 보수적 신뢰 매칭 순으로 시도.</summary>
    public static long? FindBest(string name, IReadOnlyList<(long Id, string Name)> candidates) =>
        FindUnique(name, candidates) ?? FindDefaultVariant(name, candidates) ?? FindConfident(name, candidates);

    /// <summary>수동 매칭 UI 용 추천: 토큰 자카드 유사도 상위 n개.</summary>
    public static List<(long Id, string Name, double Score)> Suggest(
        string name, IReadOnlyList<(long Id, string Name)> candidates, int n = 8)
    {
        var a = Tokens(name, false).ToHashSet();
        if (a.Count == 0) return [];
        return candidates
            .Select(c =>
            {
                var b = Tokens(c.Name, false).ToHashSet();
                var inter = a.Count(b.Contains);
                var union = a.Count + b.Count - inter;
                return (c.Id, c.Name, Score: union == 0 ? 0 : (double)inter / union);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(n)
            .ToList();
    }
}