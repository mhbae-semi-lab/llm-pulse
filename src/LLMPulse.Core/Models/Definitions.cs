namespace LLMPulse.Core.Models;

/// <summary>다국어 문자열. 누락 언어는 영어로 폴백.</summary>
public sealed class LocalizedText : Dictionary<string, string>
{
    public LocalizedText() : base(StringComparer.OrdinalIgnoreCase) { }

    public string Get(string lang)
    {
        if (TryGetValue(lang, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        var neutral = lang.Split('-')[0];
        if (TryGetValue(neutral, out v) && !string.IsNullOrWhiteSpace(v)) return v;
        return TryGetValue("en", out v) ? v : string.Empty;
    }
}

public sealed class MetricDef
{
    public string Id { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>"index" = 이미 0–100 합성 지표(그대로 사용), "percentile" = 백분위 변환.</summary>
    public string Scale { get; set; } = "percentile";
    public bool HigherIsBetter { get; set; } = true;
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public LocalizedText Summary { get; set; } = new();
    public LocalizedText Method { get; set; } = new();
    public LocalizedText Caveats { get; set; } = new();

    public bool IsPassthrough => Scale.Equals("index", StringComparison.OrdinalIgnoreCase);
}

public sealed class CategoryMetricRef
{
    public string Id { get; set; } = "";
    public double Weight { get; set; } = 1.0;
}

public sealed class CategoryDef
{
    public string Id { get; set; } = "";
    /// <summary>true 면 Best Value(허용치 내 최저가) 추천을 계산한다. 속도/가격 같은 분야는 false.</summary>
    public bool ValueMode { get; set; }
    public LocalizedText Name { get; set; } = new();
    public List<CategoryMetricRef> Metrics { get; set; } = [];
}
