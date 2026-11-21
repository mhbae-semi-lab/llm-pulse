using System.Windows;
using System.Windows.Media;

namespace LLMPulse.App.Controls;

/// <summary>값 목록을 작은 꺾은선으로 그리는 경량 컨트롤 (마지막 점에 강조 표시).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var v = Values;
        if (v is null || v.Count < 2 || ActualWidth < 4 || ActualHeight < 4) return;

        const double pad = 3;
        var min = v.Min();
        var max = v.Max();
        var span = max - min < 1e-9 ? 1 : max - min;
        var w = ActualWidth - pad * 2;
        var h = ActualHeight - pad * 2;
        Point P(int i) => new(pad + w * i / (v.Count - 1), pad + h * (1 - (v[i] - min) / span));

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (var i = 1; i < v.Count; i++) ctx.LineTo(P(i), true, true);
        }

        geo.Freeze();
        var pen = new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, geo);
        dc.DrawEllipse(Stroke, null, P(v.Count - 1), 3.5, 3.5);
    }
}