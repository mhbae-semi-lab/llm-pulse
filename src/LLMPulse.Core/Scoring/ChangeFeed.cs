namespace LLMPulse.Core.Scoring;

public enum ChangeKind { NewModel, TopChanged, EnteredTop, RankUp, RankDown }

/// <param name="OtherModelId">TopChanged 일 때 이전 1위.</param>
public sealed record ChangeItem(ChangeKind Kind, string CategoryId, long ModelId, long? OtherModelId, int? FromRank, int? ToRank);

/// <summary>두 시점의 분야별 순위를 비교해 "무엇이 바뀌었나" 목록을 만든다.</summary>
public static class ChangeFeed
{
    public static List<ChangeItem> Build(
        IReadOnlyDictionary<string, Dictionary<long, int>> previous,
        IReadOnlyDictionary<string, Dictionary<long, int>> current,
        IReadOnlyCollection<long> newModels,
        int topN = 10,
        int minMove = 3,
        int movesPerCategory = 2,
        int limit = 30)
    {
        var items = new List<ChangeItem>();
        var newSet = newModels.ToHashSet();

        // 신규 모델: 분야 하나만 대표로 (가장 높은 순위를 기록한 분야)
        foreach (var id in newModels)
        {
            var best = current
                .Where(c => c.Value.TryGetValue(id, out _))
                .Select(c => (Cat: c.Key, Rank: c.Value[id]))
                .OrderBy(x => x.Rank).FirstOrDefault();
            items.Add(new ChangeItem(ChangeKind.NewModel, best.Cat ?? "", id, null, null, best.Cat is null ? null : best.Rank));
        }

        var tops = new List<ChangeItem>();
        var moves = new List<(ChangeItem Item, int Magnitude)>();
        foreach (var (cat, cur) in current)
        {
            if (!previous.TryGetValue(cat, out var prev) || prev.Count == 0) continue;

            var curTops = cur.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet();
            var prevTops = prev.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet();
            var topChanged = curTops.Count > 0 && prevTops.Count > 0 && !curTops.Overlaps(prevTops);
            if (topChanged)
            {
                var curTop = curTops.Min();
                tops.Add(new ChangeItem(ChangeKind.TopChanged, cat, curTop, prevTops.Min(),
                    prev.TryGetValue(curTop, out var fromRank) ? fromRank : null, 1));
            }

            var catMoves = new List<(ChangeItem, int)>();
            foreach (var (id, rank) in cur)
            {
                if (rank > topN && !(prev.TryGetValue(id, out var pOld) && pOld <= topN)) continue;
                if (newSet.Contains(id)) continue;
                if (rank == 1 && topChanged) continue; // TopChanged 로 이미 표시

                if (!prev.TryGetValue(id, out var p))
                {
                    if (rank <= topN) catMoves.Add((new ChangeItem(ChangeKind.EnteredTop, cat, id, null, null, rank), topN + 1 - rank));
                    continue;
                }

                if (p > topN && rank <= topN)
                    catMoves.Add((new ChangeItem(ChangeKind.EnteredTop, cat, id, null, p, rank), topN + 1 - rank));
                else if (p - rank >= minMove && rank <= topN)
                    catMoves.Add((new ChangeItem(ChangeKind.RankUp, cat, id, null, p, rank), p - rank));
                else if (rank - p >= minMove && p <= topN)
                    catMoves.Add((new ChangeItem(ChangeKind.RankDown, cat, id, null, p, rank), rank - p));
            }

            // 오른 모델 movesPerCategory 개 + 내린 모델 1개
            var ordered = catMoves.OrderByDescending(m => m.Item2).ToList();
            moves.AddRange(ordered.Where(m => m.Item1.Kind != ChangeKind.RankDown).Take(movesPerCategory));
            moves.AddRange(ordered.Where(m => m.Item1.Kind == ChangeKind.RankDown).Take(1));
        }

        items.AddRange(tops.OrderBy(t => t.CategoryId, StringComparer.Ordinal));
        items.AddRange(moves.OrderByDescending(m => m.Magnitude).ThenBy(m => m.Item.CategoryId, StringComparer.Ordinal).Select(m => m.Item));
        return items.Take(limit).ToList();
    }
}
/// <summary>직전 이력 대비 가격이 바뀐 모델. Pct 는 (새-이전)/이전*100, 음수면 인하.</summary>
public sealed record PriceChange(long ModelId, double Old, double New, double Pct)
{
    public static PriceChange? Create(long modelId, double old, double now, double minPct)
    {
        if (old <= 0 || now <= 0) return null;
        var pct = (now - old) / old * 100;
        return Math.Abs(pct) >= minPct ? new PriceChange(modelId, old, now, pct) : null;
    }
}