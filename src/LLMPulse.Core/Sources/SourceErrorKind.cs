namespace LLMPulse.Core.Sources;

public enum SourceErrorKind { None, KeyMissing, KeyInvalid, RateLimited, Network, Other }

/// <summary>refresh_log 메시지(영문 예외 텍스트)를 사용자에게 보여줄 오류 유형으로 분류한다.</summary>
public static class SourceErrors
{
    public static SourceErrorKind Classify(string? status, string? message)
    {
        if (!string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)) return SourceErrorKind.None;
        var m = message ?? "";
        if (m.Contains("key missing", StringComparison.OrdinalIgnoreCase)) return SourceErrorKind.KeyMissing;
        if (m.Contains(" 401", StringComparison.Ordinal) || m.Contains(" 403", StringComparison.Ordinal)) return SourceErrorKind.KeyInvalid;
        if (m.Contains(" 429", StringComparison.Ordinal) || m.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return SourceErrorKind.RateLimited;
        if (m.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || m.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || m.Contains("connection", StringComparison.OrdinalIgnoreCase)
            || m.Contains("network", StringComparison.OrdinalIgnoreCase)
            || m.Contains("TaskCanceled", StringComparison.OrdinalIgnoreCase)
            || m.Contains("name resolution", StringComparison.OrdinalIgnoreCase)
            || m.Contains("actively refused", StringComparison.OrdinalIgnoreCase)) return SourceErrorKind.Network;
        return SourceErrorKind.Other;
    }
}