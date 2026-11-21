using System.Globalization;
using LLMPulse.App.Localization;

namespace LLMPulse.App;

/// <summary>현재 언어 culture 기준 숫자/가격/날짜 서식.</summary>
public static class Fmt
{
    public const string Dash = "—";

    private static CultureInfo C => Loc.Instance.Culture;

    public static string Score(double? v) => v is { } x ? x.ToString("0.0", C) : Dash;

    public static string Number(double? v, string format = "0.#") => v is { } x ? x.ToString(format, C) : Dash;

    /// <summary>USD / 1M tokens. 작은 값은 소수 3자리.</summary>
    public static string Price(double? v) => v switch
    {
        null => Dash,
        < 0.1 and > 0 => "$" + v.Value.ToString("0.000", C),
        { } x => "$" + x.ToString("0.00", C),
    };

    public static string Percent(double fraction, string format = "0.#%") => fraction.ToString(format, C);

    public static string Date(DateTimeOffset? t) =>
        t is { } x ? x.ToLocalTime().ToString("g", C) : Dash;
}
