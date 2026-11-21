using System.Text.Json;

namespace LLMPulse.Core.Sources;

/// <summary>
/// 응답 본문(페이지 목록)을 디스크에 잠시 보관한다. AA 무료 키는 하루 100회 제한이라
/// 같은 날 여러 번 갱신해도 실제 호출은 TTL 당 한 번만 나가게 한다.
/// 환경 변수 LLMPULSE_NO_CACHE=1 (또는 --force) 이면 건너뛴다.
/// </summary>
public sealed class ResponseCache
{
    private readonly string _path;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _now;

    public ResponseCache(string path, TimeSpan ttl, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _ttl = ttl;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 지금 진행 중인 갱신에만 적용되는 TTL 단축. 수동 새로고침은 "방금 나온 데이터"를 기대하므로
    /// 1시간만 지나도 다시 받게 한다 (예약/명령줄 갱신은 기본 TTL).
    /// </summary>
    public static TimeSpan? TtlOverride { get; set; }

    public static bool Disabled => Environment.GetEnvironmentVariable("LLMPULSE_NO_CACHE") is { Length: > 0 } v && v != "0";

    private sealed record Entry(DateTimeOffset FetchedAt, List<string> Pages);

    /// <summary>TTL 안의 캐시가 있으면 페이지 본문 목록을, 없으면 null.</summary>
    public IReadOnlyList<string>? TryRead()
    {
        if (Disabled || !File.Exists(_path)) return null;
        try
        {
            var e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(_path));
            if (e is null || e.Pages.Count == 0) return null;
            return _now() - e.FetchedAt < (TtlOverride is { } o && o < _ttl ? o : _ttl) ? e.Pages : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // 깨진 캐시는 없는 것으로 취급
        }
    }

    public void Write(IReadOnlyList<string> pages)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Entry(_now(), [.. pages])));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 캐시는 최적화일 뿐이므로 실패해도 갱신은 계속한다
        }
    }
}
