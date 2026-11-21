using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LLMPulse.Tests;

/// <summary>UI 문자열 리소스가 언어별로 빠짐없이 같은 키와 같은 자리표시자를 갖는지 검사한다.</summary>
public class ResourceParityTests
{
    private static string ResourcesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LLMPulse.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "LLMPulse.App", "Resources");
    }

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(ResourcesDir(), file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => d.Element("value")!.Value);

    private static string Placeholders(string s) =>
        string.Join(',', Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Distinct().Order());

    [Theory]
    [InlineData("Strings.ko.resx")]
    [InlineData("Strings.zh.resx")]
    [InlineData("Strings.fr.resx")]
    public void Translation_HasSameKeysAndPlaceholdersAsEnglish(string file)
    {
        var en = Load("Strings.resx");
        var tr = Load(file);

        Assert.Empty(en.Keys.Except(tr.Keys));
        Assert.Empty(tr.Keys.Except(en.Keys));
        foreach (var (key, value) in en)
        {
            Assert.False(string.IsNullOrWhiteSpace(tr[key]), $"{file}: {key} is empty");
            Assert.True(Placeholders(value) == Placeholders(tr[key]), $"{file}: placeholder mismatch in {key}");
        }
    }
}