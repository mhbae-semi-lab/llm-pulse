namespace LLMPulse.Core.Plans;

/// <summary>
/// 구독 플랜 목록(가격 · 포함 모델)은 자주 바뀌므로 저장소의 data/plans.json 을 갱신 때 받아 둔다.
/// 검사를 통과한 경우에만 사본을 바꾸고, 실패하면 기존 사본 또는 내장본을 그대로 쓴다.
/// </summary>
public sealed class PlanCatalogUpdater(HttpClient http, string? url = null)
{
    public const string DefaultUrl = "https://raw.githubusercontent.com/mhbae-semi-lab/llm-pulse/main/data/plans.json";
    public const string SettingStatus = "plans_remote_status";

    public string Url { get; } = url ?? (Environment.GetEnvironmentVariable("LLMPULSE_PLANS_URL") is { Length: > 0 } u ? u : DefaultUrl);

    /// <returns>성공이면 null, 실패면 사유 (사본은 바뀌지 않음).</returns>
    public async Task<string?> UpdateAsync(string remoteFile, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.GetAsync(Url, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return $"HTTP {(int)res.StatusCode}";
            var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (PlanCatalog.TryParse(json, out _) is { } error) return error;

            var tmp = remoteFile + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, remoteFile, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
