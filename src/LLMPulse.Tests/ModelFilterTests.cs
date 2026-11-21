using LLMPulse.Core;

namespace LLMPulse.Tests;

public class ModelFilterTests
{
    [Fact]
    public void NoConditions_PassesEverything() =>
        Assert.True(ModelFilter.Passes(null, null, false, null, null, false));

    [Theory]
    [InlineData(5.0, 5.0, true)]     // 경계 포함
    [InlineData(5.01, 5.0, false)]
    [InlineData(null, 5.0, false)]   // 가격 모르면 상한 조건에서 제외
    public void MaxPrice(double? price, double max, bool expected) =>
        Assert.Equal(expected, ModelFilter.Passes(price, 200, false, max, null, false));

    [Theory]
    [InlineData(200.0, true)]
    [InlineData(128.0, false)]
    [InlineData(null, false)]
    public void MinContext(double? ctx, bool expected) =>
        Assert.Equal(expected, ModelFilter.Passes(1, ctx, false, null, 200, false));

    [Fact]
    public void RecentOnly_RequiresNew()
    {
        Assert.False(ModelFilter.Passes(1, 1, false, null, null, true));
        Assert.True(ModelFilter.Passes(1, 1, true, null, null, true));
    }
}