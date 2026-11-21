using System.Text.Json;

namespace LLMPulse.Core.Usage;

/// <summary>
/// Claude Code 가 로컬에 남기는 대화 기록(~/.claude/projects/**/*.jsonl)에서 "사람이 보낸 프롬프트" 시각을 센다.
/// 네트워크/계정 정보/쿠키를 쓰지 않고, 사용자 프로필 폴더의 읽기 전용 파일만 본다.
/// 파일 단위로 (크기, 수정시각) 캐시해 매번 전체를 다시 읽지 않는다.
/// </summary>
public sealed class ClaudeCodeUsageReader
{
    private readonly string _root;
    private readonly Dictionary<string, (long Length, DateTime Mtime, List<DateTimeOffset> Prompts)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ClaudeCodeUsageReader(string? root = null) =>
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public bool Exists => Directory.Exists(_root);

    /// <summary>since 이후의 프롬프트 시각 (오름차순).</summary>
    public List<DateTimeOffset> PromptsSince(DateTimeOffset since)
    {
        var result = new List<DateTimeOffset>();
        if (!Exists) return result;

        foreach (var file in Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories))
        {
            FileInfo info;
            try { info = new FileInfo(file); } catch { continue; }
            if (info.LastWriteTimeUtc < since.UtcDateTime) continue; // 오래된 파일은 건너뜀

            if (!_cache.TryGetValue(file, out var c) || c.Length != info.Length || c.Mtime != info.LastWriteTimeUtc)
            {
                c = (info.Length, info.LastWriteTimeUtc, ScanFile(file));
                _cache[file] = c;
            }

            result.AddRange(c.Prompts.Where(t => t >= since));
        }

        result.Sort();
        return result;
    }

    public static List<DateTimeOffset> ScanFile(string path)
    {
        var list = new List<DateTimeOffset>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0 || !line.Contains("\"user\"", StringComparison.Ordinal)) continue;
                if (TryParsePrompt(line, out var at)) list.Add(at);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return list;
    }

    /// <summary>사람이 입력한 user 항목만: 도구 결과/메타/서브에이전트 메시지는 제외.</summary>
    public static bool TryParsePrompt(string jsonLine, out DateTimeOffset at)
    {
        at = default;
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "user") return false;
            if (root.TryGetProperty("toolUseResult", out _)) return false;
            if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return false;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return false;
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) return false;

            var hasText = content.ValueKind switch
            {
                JsonValueKind.String => !string.IsNullOrWhiteSpace(content.GetString()),
                JsonValueKind.Array => content.EnumerateArray().Any(b =>
                    b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out var bt) && bt.GetString() == "text"),
                _ => false,
            };
            if (!hasText) return false;

            return root.TryGetProperty("timestamp", out var ts)
                   && DateTimeOffset.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.RoundtripKind, out at);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>한도 기간 창의 시작 시각 (5h: 지난 5시간 안 첫 프롬프트, day/week/month: 달력 경계).</summary>
    public static DateTimeOffset WindowStart(string period, DateTimeOffset now)
    {
        var n = now.ToLocalTime();
        return period switch
        {
            "5h" => now - TimeSpan.FromHours(5),
            "day" => new DateTimeOffset(n.Date),
            "week" => new DateTimeOffset(n.Date.AddDays(-(((int)n.DayOfWeek + 6) % 7))),
            _ => new DateTimeOffset(new DateTime(n.Year, n.Month, 1)),
        };
    }
}