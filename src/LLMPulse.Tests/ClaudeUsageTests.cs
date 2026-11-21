using LLMPulse.Core.Usage;

namespace LLMPulse.Tests;

public class ClaudeUsageTests
{
    [Theory]
    [InlineData("{\"type\":\"user\",\"timestamp\":\"2026-11-19T01:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}", true)]
    [InlineData("{\"type\":\"user\",\"timestamp\":\"2026-11-19T01:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}", true)]
    [InlineData("{\"type\":\"user\",\"timestamp\":\"2026-11-19T01:00:00.000Z\",\"toolUseResult\":{},\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\"}]}}", false)]
    [InlineData("{\"type\":\"user\",\"isMeta\":true,\"timestamp\":\"2026-11-19T01:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"x\"}}", false)]
    [InlineData("{\"type\":\"user\",\"isSidechain\":true,\"timestamp\":\"2026-11-19T01:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"x\"}}", false)]
    [InlineData("{\"type\":\"assistant\",\"timestamp\":\"2026-11-19T01:00:00.000Z\",\"message\":{\"role\":\"assistant\",\"content\":\"x\"}}", false)]
    [InlineData("not json", false)]
    public void TryParsePrompt_CountsOnlyHumanPrompts(string line, bool expected) =>
        Assert.Equal(expected, ClaudeCodeUsageReader.TryParsePrompt(line, out _));

    [Fact]
    public void PromptsSince_ReadsFilesAndFiltersByTime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llmpulse-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "proj"));
        try
        {
            File.WriteAllLines(Path.Combine(dir, "proj", "a.jsonl"),
            [
                "{\"type\":\"user\",\"timestamp\":\"2026-11-19T01:00:00.000Z\",\"message\":{\"content\":\"old\"}}",
                "{\"type\":\"assistant\",\"timestamp\":\"2026-11-19T01:00:05.000Z\",\"message\":{\"content\":\"a\"}}",
                "{\"type\":\"user\",\"timestamp\":\"2026-11-19T06:00:00.000Z\",\"message\":{\"content\":\"new\"}}",
            ]);
            var r = new ClaudeCodeUsageReader(dir);
            var all = r.PromptsSince(DateTimeOffset.Parse("2026-11-19T00:00:00Z"));
            Assert.Equal(2, all.Count);
            var recent = r.PromptsSince(DateTimeOffset.Parse("2026-11-19T05:00:00Z"));
            Assert.Single(recent);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MissingFolder_ReturnsEmpty() =>
        Assert.Empty(new ClaudeCodeUsageReader(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"))).PromptsSince(DateTimeOffset.UnixEpoch));
}