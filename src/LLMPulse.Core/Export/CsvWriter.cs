using System.Text;

namespace LLMPulse.Core.Export;

/// <summary>RFC 4180 방식 CSV. 외부 소스에서 온 텍스트가 수식으로 실행되지 않도록 방어한다.</summary>
public static class CsvWriter
{
    /// <summary>텍스트 셀: 스프레드시트가 수식으로 해석하는 접두(= + - @ 탭 CR)는 작은따옴표로 무력화한다.</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
    }

    public static string Escape(string? value)
    {
        value ??= "";
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static string Build(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', header.Select(Escape))).Append("\r\n");
        foreach (var row in rows) sb.Append(string.Join(',', row.Select(Escape))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>숫자는 문화권과 무관하게 점(.) 소수로 내보낸다. 값이 없으면 빈 칸.</summary>
    public static string Number(double? v, string format = "0.###") =>
        v is { } x ? x.ToString(format, System.Globalization.CultureInfo.InvariantCulture) : "";
}