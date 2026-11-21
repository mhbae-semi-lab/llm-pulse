using LLMPulse.Core.Plans;

namespace LLMPulse.Tests;

public class LimitResetTests
{
    private static DateTimeOffset Local(int y, int mo, int d, int h = 12) => new(new DateTime(y, mo, d, h, 0, 0));

    [Fact]
    public void FiveHour_NoUse_HasNoReset() =>
        Assert.Null(new PlanLimit("p", 100, "5h", 0, null, "").ResetAt(Local(2026, 11, 19)));

    [Fact]
    public void FiveHour_AfterUse_IsStartPlusFiveHours()
    {
        var start = Local(2026, 11, 19, 9);
        var r = new PlanLimit("p", 100, "5h", 3, start, "").ResetAt(Local(2026, 11, 19, 10));
        Assert.Equal(start + TimeSpan.FromHours(5), r);
    }

    [Fact]
    public void Day_ResetsAtNextMidnight() =>
        Assert.Equal(Local(2026, 11, 20, 0), new PlanLimit("p", 1, "day", 0, null, "").ResetAt(Local(2026, 11, 19)));

    [Fact]
    public void Week_ResetsNextMonday() =>
        // 2026-11-19 is Thursday -> next Monday 2026-11-23
        Assert.Equal(Local(2026, 11, 23, 0), new PlanLimit("p", 1, "week", 0, null, "").ResetAt(Local(2026, 11, 19)));

    [Fact]
    public void Month_ResetsFirstOfNextMonth() =>
        Assert.Equal(Local(2026, 12, 1, 0), new PlanLimit("p", 1, "month", 0, null, "").ResetAt(Local(2026, 11, 19)));
}