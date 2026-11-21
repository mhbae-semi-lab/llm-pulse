namespace LLMPulse.Core;

/// <summary>리더보드의 가격/컨텍스트/최근 출시 조건. 값이 없는 모델은 해당 조건이 켜져 있으면 제외한다.</summary>
public static class ModelFilter
{
    public static bool Passes(double? price, double? contextK, bool isNew,
        double? maxPrice, double? minContextK, bool recentOnly) =>
        (!recentOnly || isNew)
        && (maxPrice is not { } p || (price is { } pr && pr <= p))
        && (minContextK is not { } c || (contextK is { } ck && ck >= c));
}