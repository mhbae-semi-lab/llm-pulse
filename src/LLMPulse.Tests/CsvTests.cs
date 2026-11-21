using LLMPulse.Core.Export;

namespace LLMPulse.Tests;

public class CsvTests
{
    [Fact]
    public void Build_QuotesCommasQuotesAndNewlines()
    {
        var csv = CsvWriter.Build(["a", "b,c"], [["x\"y", "line1\nline2"], ["1", ""]]);
        Assert.Equal("a,\"b,c\"\r\n\"x\"\"y\",\"line1\nline2\"\r\n1,\r\n", csv);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "'=HYPERLINK(\"http://x\")")]
    [InlineData("+1", "'+1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("-2", "'-2")]
    [InlineData("GPT-5", "GPT-5")]
    [InlineData(null, "")]
    public void Text_NeutralizesFormulaPrefixes(string? input, string expected) =>
        Assert.Equal(expected, CsvWriter.Text(input));

    [Fact]
    public void Number_IsInvariantAndBlankWhenMissing()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            Assert.Equal("1.5", CsvWriter.Number(1.5));
            Assert.Equal("", CsvWriter.Number(null));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }
}