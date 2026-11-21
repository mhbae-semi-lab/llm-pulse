using LLMPulse.Core;

namespace LLMPulse.Tests;

public class ReleaseDatesTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    [Theory]
    [InlineData("2026-11-20", true)]
    [InlineData("2026-10-21", true)]    // 정확히 30일 전
    [InlineData("2026-10-20", false)]   // 31일 전
    [InlineData("2026-11-21", false)]   // 미래
    [InlineData("garbage", false)]
    [InlineData(null, false)]
    public void IsRecent(string? release, bool expected) =>
        Assert.Equal(expected, ReleaseDates.IsRecent(release, Today, 30));
}