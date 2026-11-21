using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LLMPulse.App.Localization;

namespace LLMPulse.App;

/// <summary>ì ìˆ˜(0â€“100) â†’ ë‹¨ì¼ ìƒ‰ìƒ ê³„ì—´ ë†ë„ (ìƒ‰ì•½ ê³ ë ¤: ìƒ‰ì¡° ëŒ€ì‹  ëª…ë„/íˆ¬ëª…ë„, ìˆ«ì ë³‘ê¸°).</summary>
public sealed class HeatBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double score) return Brushes.Transparent;
        var alpha = (byte)Math.Round(Math.Clamp(score, 0, 100) / 100.0 * 140);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0x2E, 0x8B, 0xF0));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>ê°’ â†’ í˜„ì¬ ì–¸ì–´ í˜•ì‹ ë¬¸ìì—´. parameter = ì„œì‹(ì˜ˆ: "0.0"). null ì´ë©´ "â€”".</summary>
public sealed class NumberTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double d) return Fmt.Dash;
        return parameter as string == "price" ? Fmt.Price(d) : d.ToString(parameter as string ?? "0.#", Loc.Instance.Culture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>°ªÀÌ ÀÖÀ¸¸é null(ÅøÆÁ ¾øÀ½), ¾øÀ¸¸é parameter ¹®ÀÚ¿­ ? ºó Ä­ÀÇ ÀÌÀ¯¸¦ ¾Ë·Á ÁÙ ¶§ ¾´´Ù.</summary>
public sealed class MissingValueTipConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? parameter : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
