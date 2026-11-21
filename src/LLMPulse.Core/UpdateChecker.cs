using System.Net;
using System.Text.Json;

namespace LLMPulse.Core;

public sealed record UpdateInfo(string Tag, string Url, bool IsNewer);

/// <summary>GitHub 최신 릴리스 태그를 조회해 현재 버전과 비교한다. 사용자가 직접 누를 때만 호출한다 (자동 다운로드 없음).</summary>
public sealed class UpdateChecker(HttpClient http, string repo = "mhbae-semi-lab/llm-pulse")
{
    /// <returns>릴리스가 없으면 null (404).</returns>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        req.Headers.UserAgent.ParseAdd("LLMPulse");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var url = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new UpdateInfo(tag, url, IsNewer(tag, current));
    }

    public static bool IsNewer(string tag, Version current)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) t = t[..dash];
        return Version.TryParse(t, out var v) && v > current;
    }
}