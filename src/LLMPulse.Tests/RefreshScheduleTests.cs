using System.Xml.Linq;
using LLMPulse.Core.Scheduling;

namespace LLMPulse.Tests;

public class RefreshScheduleTests
{
    private static readonly TimeOnly Eight = new(8, 0);

    private static DateTimeOffset Local(int y, int m, int d, int h, int min) =>
        new(new DateTime(y, m, d, h, min, 0, DateTimeKind.Local));

    [Theory]
    [InlineData("08:00", 8, 0)]
    [InlineData("8:05", 8, 5)]
    [InlineData(" 23:59 ", 23, 59)]
    public void TryParseTime_Accepts(string text, int h, int m)
    {
        Assert.True(RefreshSchedule.TryParseTime(text, out var t));
        Assert.Equal(new TimeOnly(h, m), t);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("25:00")]
    [InlineData("8")]
    [InlineData("abc")]
    public void TryParseTime_Rejects(string? text) => Assert.False(RefreshSchedule.TryParseTime(text, out _));

    [Fact]
    public void IsDue_FalseBeforeTodaysSlot() =>
        Assert.False(RefreshSchedule.IsDue(Local(2026, 11, 19, 7, 59), Local(2026, 11, 18, 8, 1), Eight));

    [Fact]
    public void IsDue_TrueAfterSlotWhenLastRefreshWasYesterday() =>
        Assert.True(RefreshSchedule.IsDue(Local(2026, 11, 19, 8, 0), Local(2026, 11, 18, 8, 1), Eight));

    [Fact]
    public void IsDue_FalseWhenAlreadyRefreshedAfterSlot() =>
        Assert.False(RefreshSchedule.IsDue(Local(2026, 11, 19, 15, 0), Local(2026, 11, 19, 8, 2), Eight));

    [Fact]
    public void IsDue_TrueWhenRefreshedTodayButBeforeSlot() =>
        Assert.True(RefreshSchedule.IsDue(Local(2026, 11, 19, 9, 0), Local(2026, 11, 19, 6, 0), Eight));

    [Fact]
    public void IsDue_TrueWhenNeverRefreshed() =>
        Assert.True(RefreshSchedule.IsDue(Local(2026, 11, 19, 12, 0), null, Eight));

    [Fact]
    public void BuildTaskXml_IsWellFormedAndEscapesPath()
    {
        var xml = RefreshSchedule.BuildTaskXml(@"C:\Apps\R&D <x>\LLMPulse.App.exe", new TimeOnly(8, 30), new DateTime(2026, 11, 19));
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(@"C:\Apps\R&D <x>\LLMPulse.App.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--refresh", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("2026-11-19T08:30:00", doc.Descendants(ns + "StartBoundary").Single().Value);
        Assert.Equal("true", doc.Descendants(ns + "StartWhenAvailable").Single().Value);
    }
}