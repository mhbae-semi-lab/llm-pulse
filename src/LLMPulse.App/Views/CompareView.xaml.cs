using System.Windows.Controls;
using LLMPulse.App.Charts;
using LLMPulse.App.Localization;
using LLMPulse.App.ViewModels;
using ScottPlot;

namespace LLMPulse.App.Views;

public partial class CompareView : UserControl
{
    private CompareViewModel? _vm;

    public CompareView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => { ChartTheme.Changed += Draw; Draw(); };
        Unloaded += (_, _) => ChartTheme.Changed -= Draw;
    }

    private void Attach()
    {
        if (_vm is not null) _vm.Updated -= Draw;
        _vm = DataContext as CompareViewModel;
        if (_vm is not null) _vm.Updated += Draw;
        Draw();
    }

    private void Draw()
    {
        if (_vm is null) return;
        DrawRadar();
        DrawTrend();
        DrawDomains();
    }

    /// <summary>AA 전문 분야 지수(금융·법률 등)를 모델별 묶음 막대로. 값 없는 분야는 막대 없이 둔다.</summary>
    private void DrawDomains()
    {
        if (_vm is null) return;
        var plot = DomainPlot.Plot;
        plot.Clear();
        ChartTheme.Apply(plot);

        var axes = _vm.DomainAxes;
        var count = _vm.Models.Count;
        var any = axes.Any(a => _vm.Models.Any(m => m.Scores[a] is not null));
        DomainPanel.Visibility = any ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (any)
        {
            var width = 0.8 / count;
            for (var i = 0; i < count; i++)
            {
                var m = _vm.Models[i];
                var color = ChartTheme.Series[i % ChartTheme.Series.Length];
                var bars = new List<Bar>();
                for (var k = 0; k < axes.Count; k++)
                {
                    if (m.Scores[axes[k]] is not { } v) continue;
                    bars.Add(new Bar
                    {
                        Position = k + (i - (count - 1) / 2.0) * width,
                        Value = v,
                        Size = width * 0.9,
                        FillColor = color,
                    });
                }
                var bp = plot.Add.Bars(bars);
                bp.LegendText = m.Name;
            }

            var ticks = axes.Select((a, k) => new Tick(k, _vm.CategoryNames[a].Replace(" & ", "\n& "))).ToArray();
            plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
            plot.Axes.SetLimits(-0.6, axes.Count - 0.4, 0, 100);
            plot.ShowLegend(Alignment.UpperRight);
        }

        DomainPlot.Refresh();
    }

    private static MarkerShape ShapeFor(int i) => i switch
    {
        0 => MarkerShape.FilledCircle,
        1 => MarkerShape.FilledSquare,
        2 => MarkerShape.FilledTriangleUp,
        _ => MarkerShape.FilledDiamond,
    };

    /// <summary>분야별 점수 레이더 (0–100). 데이터 없는 분야는 0 으로 그리고 도움말에 명시.</summary>
    private void DrawRadar()
    {
        if (_vm is null) return;
        var plot = RadarPlot.Plot;
        plot.Clear();
        ChartTheme.Apply(plot);
        plot.HideGrid();
        plot.Axes.Frameless();

        var axes = _vm.RadarAxes;
        var n = axes.Count;
        if (n >= 3 && _vm.Models.Count > 0)
        {
            double Ang(int k) => Math.PI / 2 - 2 * Math.PI * k / n;

            // 동심 다각형 (25/50/75/100) + 축선 + 축 이름
            foreach (var ring in new[] { 25, 50, 75, 100 })
            {
                var xs = Enumerable.Range(0, n + 1).Select(k => ring * Math.Cos(Ang(k % n))).ToArray();
                var ys = Enumerable.Range(0, n + 1).Select(k => ring * Math.Sin(Ang(k % n))).ToArray();
                var s = plot.Add.Scatter(xs, ys);
                s.MarkerSize = 0;
                s.LineWidth = 1;
                s.Color = ChartTheme.Grid;
            }

            for (var k = 0; k < n; k++)
            {
                var spoke = plot.Add.Line(0, 0, 100 * Math.Cos(Ang(k)), 100 * Math.Sin(Ang(k)));
                spoke.Color = ChartTheme.Grid;
                spoke.LineWidth = 1;

                // 긴 이름은 괄호 앞에서 줄바꿈하고, 축이 놓인 방향에 따라 글자가 바깥쪽으로 뻗게 해서 서로 겹치지 않게 한다
                double cx = Math.Cos(Ang(k)), cy = Math.Sin(Ang(k));
                var label = plot.Add.Text(_vm.CategoryNames[axes[k]].Replace(" (", "\n("), 110 * cx, 108 * cy);
                label.LabelFontColor = ChartTheme.Text;
                label.LabelFontSize = 14;
                label.LabelAlignment = (cx > 0.15 ? 1 : cx < -0.15 ? -1 : 0, cy > 0.7 ? 1 : cy < -0.7 ? -1 : 0) switch
                {
                    (1, 1) => Alignment.LowerLeft, (1, -1) => Alignment.UpperLeft, (1, _) => Alignment.MiddleLeft,
                    (-1, 1) => Alignment.LowerRight, (-1, -1) => Alignment.UpperRight, (-1, _) => Alignment.MiddleRight,
                    (_, 1) => Alignment.LowerCenter, (_, -1) => Alignment.UpperCenter, _ => Alignment.MiddleCenter,
                };
            }

            for (var i = 0; i < _vm.Models.Count; i++)
            {
                var m = _vm.Models[i];
                var xs = Enumerable.Range(0, n + 1).Select(k => (m.Scores[axes[k % n]] ?? 0) * Math.Cos(Ang(k % n))).ToArray();
                var ys = Enumerable.Range(0, n + 1).Select(k => (m.Scores[axes[k % n]] ?? 0) * Math.Sin(Ang(k % n))).ToArray();
                var s = plot.Add.Scatter(xs, ys);
                s.Color = ChartTheme.Series[i % ChartTheme.Series.Length];
                s.LineWidth = 2.5f;
                s.MarkerSize = 9;
                s.MarkerShape = ShapeFor(i);
                s.LegendText = m.Name;
            }

            // 범례는 차트 아래 XAML 행에 이미 있으므로 레이더 안에서는 숨겨 라벨을 가리지 않게 한다
            plot.HideLegend();
            plot.Axes.SetLimits(-215, 215, -165, 165);
            plot.Axes.SquareUnits();
        }

        RadarPlot.Refresh();
    }

    /// <summary>스냅샷 기반 일별 추이 (선택한 지표).</summary>
    private void DrawTrend()
    {
        if (_vm is null) return;
        var plot = TrendPlot.Plot;
        plot.Clear();
        ChartTheme.Apply(plot);

        var anyPoints = false;
        var maxPoints = 0;
        for (var i = 0; i < _vm.Models.Count; i++)
        {
            var m = _vm.Models[i];
            var hist = _vm.History(m.Id);
            maxPoints = Math.Max(maxPoints, hist.Count);
            if (hist.Count == 0) continue;
            anyPoints = true;

            var xs = hist.Select(h => h.Date.ToDateTime(TimeOnly.MinValue).ToOADate()).ToArray();
            var ys = hist.Select(h => h.Value).ToArray();
            var s = plot.Add.Scatter(xs, ys);
            s.Color = ChartTheme.Series[i % ChartTheme.Series.Length];
            s.LineWidth = 2.5f;
            s.MarkerSize = 8;
            s.MarkerShape = ShapeFor(i);
            s.LegendText = m.Name;
        }

        if (anyPoints)
        {
            plot.Axes.DateTimeTicksBottom();
            plot.ShowLegend(Alignment.LowerRight);
            plot.Axes.AutoScale();
        }

        TrendNote.Text = !anyPoints || maxPoints < 2 ? Loc.Instance["Compare_TrendFew"] : "";
        TrendPlot.Refresh();
    }
}
