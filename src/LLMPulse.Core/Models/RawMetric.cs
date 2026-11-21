namespace LLMPulse.Core.Models;

/// <summary>소스에서 가져온 단일 지표 값. 정규화 전 원본.</summary>
public sealed record RawMetric(string MetricId, double Value);

/// <summary>한 소스에서 매칭 단위로 식별되는 모델 + 지표/가격.</summary>
public sealed class FetchedModel
{
    public required string SourceName { get; init; }
    public required string CanonicalName { get; init; }
    public string? Vendor { get; init; }
    public string? ReleaseDate { get; init; }
    public IReadOnlyList<RawMetric> Metrics { get; init; } = [];
    public double? InputPerM { get; init; }
    public double? OutputPerM { get; init; }

    /// <summary>블렌디드 3:1. Free API 에 없으면 입력/출력으로 계산.</summary>
    public double? BlendedPerM =>
        InputPerM is { } i && OutputPerM is { } o ? (3 * i + o) / 4.0 : null;
}

public sealed class SourceFetchResult
{
    public required string SourceId { get; init; }
    public IReadOnlyList<FetchedModel> Models { get; init; } = [];

    /// <summary>true 면 이미 알려진 모델에만 붙이고 새 모델은 만들지 않는다 (보강용 소스).</summary>
    public bool MatchOnly { get; init; }

    /// <summary>갱신 로그에 덧붙일 짧은 메모 (예: "cached" = 디스크 캐시 사용, API 호출 없음).</summary>
    public string? Note { get; init; }

    /// <summary>소스 데이터 자체의 기준일 (리더보드 게시일 등). 실시간 API 는 null.</summary>
    public DateOnly? DataAsOf { get; init; }
}
