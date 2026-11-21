using LLMPulse.Core.Sources;
using Xunit;

namespace LLMPulse.Tests;

public class SourceErrorTests
{
    [Theory]
    [InlineData("ok", "done", SourceErrorKind.None)]
    [InlineData("error", "Artificial Analysis API key missing. Run with --set-api-key", SourceErrorKind.KeyMissing)]
    [InlineData("error", "AA API 401 Unauthorized: {}", SourceErrorKind.KeyInvalid)]
    [InlineData("error", "AA API 403 Forbidden: {}", SourceErrorKind.KeyInvalid)]
    [InlineData("error", "AA API 429 Too Many Requests: limit", SourceErrorKind.RateLimited)]
    [InlineData("error", "No such host is known.", SourceErrorKind.Network)]
    [InlineData("error", "The request was canceled due to the configured timeout; timed out", SourceErrorKind.Network)]
    [InlineData("error", "something odd", SourceErrorKind.Other)]
    public void Classify(string status, string message, SourceErrorKind expected) =>
        Assert.Equal(expected, SourceErrors.Classify(status, message));
}