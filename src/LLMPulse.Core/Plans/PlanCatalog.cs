using System.Text.Json;
using LLMPulse.Core.Models;
using LLMPulse.Core.Scoring;

namespace LLMPulse.Core.Plans;

/// <summary>구독/접근 수단. 모델 매핑은 수동 관리 데이터이며 제작사/이름 규칙으로 근사한다.</summary>
public sealed class PlanDef
{
    public string Id { get; set; } = "";
    public double? MonthlyUsd { get; set; }
    /// <summary>true 면 모든 모델 (API 종량제).</summary>
    public bool All { get; set; }
    /// <summary>제작사 이름에 이 중 하나가 포함되면 후보 (비어 있으면 제작사 제한 없음).</summary>
    public List<string> Vendors { get; set; } = [];
    /// <summary>모델 이름에 이 중 하나가 포함돼야 함 (비어 있으면 제한 없음).</summary>
    public List<string> Include { get; set; } = [];
    /// <summary>모델 이름에 이 중 하나라도 포함되면 제외.</summary>
    public List<string> Exclude { get; set; } = [];
    public string Reviewed { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public LocalizedText Note { get; set; } = new();
}

public sealed class PlanCatalog
{
    public const string UserFileName = "plans.user.json";
    /// <summary>저장소의 data/plans.json 을 받아 둔 사본 (갱신 때 내려받음, 다음 실행부터 적용).</summary>
    public const string RemoteFileName = "plans.remote.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<PlanDef> Plans { get; }

    /// <summary>사용자 파일을 읽다 실패했을 때 사유 (내장 목록으로 대체됨).</summary>
    public string? LoadWarning { get; }

    public bool IsUserDefined { get; }

    /// <summary>true 면 원격에서 받아 둔 목록을 쓰는 중.</summary>
    public bool IsRemote { get; }

    public PlanCatalog(IReadOnlyList<PlanDef> plans, string? warning = null, bool isUserDefined = false, bool isRemote = false)
    {
        Plans = plans;
        LoadWarning = warning;
        IsUserDefined = isUserDefined;
        IsRemote = isRemote;
    }

    /// <summary>
    /// 우선순위: 사용자 파일(직접 편집) → 원격 사본(remoteFile) → 내장 plans.json.
    /// 사용자 파일이 깨졌으면 경고와 함께 다음 순위를 쓴다. 원격 사본이 깨졌으면 조용히 내장본을 쓴다.
    /// </summary>
    public static PlanCatalog Load(string? userFile = null, string? remoteFile = null)
    {
        string? warning = null;
        if (userFile is not null && File.Exists(userFile))
        {
            try
            {
                var error = TryParse(File.ReadAllText(userFile), out var user);
                if (error is null) return new PlanCatalog(user, null, true);
                warning = $"{UserFileName}: {error}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning = $"{UserFileName}: {ex.Message}";
            }
        }

        try
        {
            if (remoteFile is not null && File.Exists(remoteFile) && TryParse(File.ReadAllText(remoteFile), out var remote) is null)
                return new PlanCatalog(remote, warning, false, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new PlanCatalog(ReadEmbedded(), warning);
    }

    /// <summary>플랜 목록 JSON 검사. 성공이면 null, 실패면 사유.</summary>
    public static string? TryParse(string json, out List<PlanDef> plans)
    {
        plans = [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<PlanDef>>(json, Json);
            if (parsed is null || parsed.Count == 0) return "empty list.";
            var bad = parsed.FindIndex(p => string.IsNullOrWhiteSpace(p.Id));
            if (bad >= 0) return $"plan #{bad + 1} has no id.";
            plans = parsed;
            return null;
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }
    }

    public static bool Matches(PlanDef plan, ModelInput model)
    {
        if (plan.All) return true;

        if (plan.Vendors.Count > 0
            && !(model.Vendor is { } v && plan.Vendors.Any(x => v.Contains(x, StringComparison.OrdinalIgnoreCase))))
            return false;
        if (plan.Include.Count > 0
            && !plan.Include.Any(x => model.Name.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (plan.Exclude.Any(x => model.Name.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return false;

        // 제한 규칙이 하나도 없는 플랜이 모든 모델과 매칭되는 사고를 막는다
        return plan.Vendors.Count > 0 || plan.Include.Count > 0;
    }

    /// <summary>선택한 플랜 중 하나라도 허용하는 모델이면 true. 선택이 없으면 null (필터 없음).</summary>
    public Func<ModelInput, bool>? Filter(IEnumerable<string> selectedIds)
    {
        var chosen = Plans.Where(p => selectedIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        return chosen.Count == 0 ? null : m => chosen.Any(p => Matches(p, m));
    }

    private static List<PlanDef> ReadEmbedded()
    {
        using var s = typeof(PlanCatalog).Assembly.GetManifestResourceStream("data.plans.json")
            ?? throw new InvalidOperationException("Embedded resource 'data.plans.json' not found.");
        return JsonSerializer.Deserialize<List<PlanDef>>(s, Json) ?? [];
    }
}