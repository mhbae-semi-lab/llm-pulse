using LLMPulse.Core.Models;

namespace LLMPulse.Core.Sources;

public interface ISourceAdapter
{
    string SourceId { get; }
    Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>Free API 에서 저장하는 지표 id. metrics.json 과 맞출 예정(M3).</summary>
public static class MetricIds
{
    public const string IntelligenceIndex = "aa_intelligence_index";
    public const string CodingIndex = "aa_coding_index";
    public const string AgenticIndex = "aa_agentic_index";
    public const string FinanceIndex = "aa_finance_index";
    public const string StrategyIndex = "aa_strategy_index";
    public const string LegalIndex = "aa_legal_index";
    public const string HealthcareIndex = "aa_healthcare_index";
    public const string EngineeringIndex = "aa_engineering_index";
    public const string EconomicsIndex = "aa_economics_index";
    public const string OutputTokensPerSecond = "output_tok_s";
    public const string TimeToFirstToken = "ttft_s";
    public const string ContextK = "context_k";
    public const string AiderPolyglot = "aider_polyglot";
    public const string LmArenaOverall = "lmarena_overall";
    public const string LmArenaCoding = "lmarena_coding";
    public const string LmArenaKorean = "lmarena_korean";
    public const string LmArenaMath = "lmarena_math";
    public const string LmArenaHard = "lmarena_hard";
    public const string LmArenaInstruction = "lmarena_instruction";
}
