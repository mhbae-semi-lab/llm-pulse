using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LLMPulse.App.Charts;
using LLMPulse.App.Localization;
using LLMPulse.App.ViewModels;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.TickGenerators;

namespace LLMPulse.App.Views;

public partial class CategoryDetailView : UserControl
{
    private CategoryDetailViewModel? _vm;
    private Scatter? _allPoints;
    private IReadOnlyList<PricePoint> _points = [];
    private System.Windows.Point _downPos;
    private bool Log => LogToggle.IsChecked == true;

    private void OnLogToggle(object sender, RoutedEventArgs e) => DrawScatter(_points);

    /// <summary>드래그(이동)가 아닌 클릭일 때만 가장 가까운 점의 모델 상세로 이동.</summary>
    private void OnScatterClick(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(ScatterPlot);
        if (_vm is null || _allPoints is null || (pos - _downPos).Length > 4) return;
        var scale = ScatterPlot.DisplayScale;
        var coords = ScatterPlot.Plot.GetCoordinates(new Pixel((float)(pos.X * scale), (float)(pos.Y * scale)));
        var nearest = _allPoints.Data.GetNearest(coords, ScatterPlot.Plot.LastRender, 24);
        if (nearest.IsReal) _vm.OpenModel(_points[nearest.Index].ModelId);
    }

    public CategoryDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => { ChartTheme.Changed += Draw; Draw(); };
        Unloaded += (_, _) => ChartTheme.Changed -= Draw;
        ScatterPlot.MouseMove += OnScatterMouseMove;
        ScatterPlot.MouseLeftButtonDown += (_, e) => _downPos = e.GetPosition(ScatterPlot);
        ScatterPlot.MouseLeftButtonUp += OnScatterClick;
        ScatterPlot.MouseLeave += (_, _) => HoverText.Text = "";
    }

    private void Attach()
    {
        if (_vm is not null) _vm.Updated -= Draw;
        _vm = DataContext as CategoryDetailViewModel;
        if (_vm is not null) _vm.Updated += Draw;
        Draw();
    }

    private void Draw()
    {
        if (_vm is null) return;
        DrawBars(_vm.Bars);
        ScatterPanel.Visibility = _vm.ShowScatter ? Visibility.Visible : Visibility.Collapsed;
        DrawScatter(_vm.Points);
    }

    private void DrawBars(IReadOnlyList<BarItem> items)
    {
        var plot = BarPlot.Plot;
        plot.Clear();
        ChartTheme.Apply(plot);

        if (items.Count > 0)
        {
            // 1위가 맨 위로 오도록 위치를 뒤집는다
            var bars = items.Select((b, i) => new Bar
            {
                Position = items.Count - i,
                Value = b.Score,
                FillColor = i == 0 ? ChartTheme.Highlight : b.InPlan ? ChartTheme.Mine : ChartTheme.Accent,
                Size = 0.7,
                LineColor = b.Favorite ? ChartTheme.Text : Colors.Transparent,
                LineWidth = b.Favorite ? 2.5f : 0,
            }).ToArray();

            var barPlot = plot.Add.Bars(bars);
            barPlot.Horizontal = true;

            plot.Axes.Left.TickGenerator = new NumericManual(
                items.Select((b, i) => (Position: (double)(items.Count - i), Label: Trim(b.Name))).Select(t => new Tick(t.Position, t.Label)).ToArray());
            plot.Axes.Bottom.Label.Text = Loc.Instance["Chart_ScoreAxis"];
            plot.Axes.SetLimits(0, 100, 0.2, items.Count + 0.8);

            // 막대 끝에 숫자 병기 (색에만 의존하지 않음)
            for (var i = 0; i < items.Count; i++)
            {
                var t = plot.Add.Text(Fmt.Score(items[i].Score), Math.Min(items[i].Score + 1.5, 92), items.Count - i);
                t.LabelFontColor = ChartTheme.Text;
                t.LabelFontSize = 14;
                t.LabelAlignment = Alignment.MiddleLeft;
            }
        }

        BarPlot.Refresh();
    }

    private void DrawScatter(IReadOnlyList<PricePoint> points)
    {
        _points = points;
        _allPoints = null;
        var plot = ScatterPlot.Plot;
        plot.Clear();
        ChartTheme.Apply(plot);

        if (points.Count > 0)
        {
            var log = Log;
            double X(PricePoint p) => log ? Math.Log10(Math.Max(p.Price, 0.01)) : p.Price;

            _allPoints = plot.Add.Scatter(points.Select(X).ToArray(), points.Select(p => p.Score).ToArray());
            _allPoints.LineWidth = 0;
            _allPoints.MarkerSize = 8;
            _allPoints.Color = ChartTheme.Muted;

            // 내 구독에서 쓸 수 있는 모델은 초록, 즐겨찾기는 흰 테두리 점으로 강조
            var someOutside = points.Any(p => !p.InPlan);
            var mine = someOutside ? points.Where(p => p.InPlan).ToArray() : [];
            if (mine.Length > 0)
            {
                var ms = plot.Add.Scatter(mine.Select(X).ToArray(), mine.Select(p => p.Score).ToArray());
                ms.LineWidth = 0;
                ms.MarkerSize = 9;
                ms.Color = ChartTheme.Mine;
                ms.LegendText = Loc.Instance["Chart_InPlanLegend"];
            }

            foreach (var p in points.Where(p => p.Favorite))
            {
                var fm = plot.Add.Marker(X(p), p.Score);
                fm.MarkerShape = MarkerShape.OpenCircle;
                fm.MarkerSize = 16;
                fm.MarkerLineWidth = 2.5f;
                fm.Color = ChartTheme.Text;
            }

            var frontier = points.Where(p => p.Pareto).OrderBy(p => p.Price).ToArray();
            if (frontier.Length > 0)
            {
                var f = plot.Add.Scatter(frontier.Select(X).ToArray(), frontier.Select(p => p.Score).ToArray());
                f.LineWidth = 2;
                f.MarkerSize = 11;
                f.MarkerShape = MarkerShape.FilledDiamond;
                f.Color = ChartTheme.Accent;
                f.LegendText = Loc.Instance["Chart_ParetoLegend"];
            }

            foreach (var p in points.Where(p => p.IsBest || p.IsValue))
            {
                var m = plot.Add.Marker(X(p), p.Score);
                m.MarkerShape = p.IsBest ? MarkerShape.FilledSquare : MarkerShape.FilledCircle;
                m.MarkerSize = 15;
                m.Color = ChartTheme.Highlight;
                var label = plot.Add.Text(p.Name, X(p), p.Score);
                label.LabelFontColor = ChartTheme.Text;
                label.LabelFontSize = 14;
                label.LabelAlignment = Alignment.MiddleRight;
                label.OffsetX = -16;
            }

            plot.Axes.Bottom.TickGenerator = log
                ? new NumericAutomatic
                {
                    LabelFormatter = v => "$" + Math.Pow(10, v).ToString(v < 0 ? "0.##" : "0.#", Loc.Instance.Culture),
                }
                : new NumericAutomatic
                {
                    LabelFormatter = v => "$" + v.ToString("0.##", Loc.Instance.Culture),
                };
            plot.Axes.Bottom.Label.Text = Loc.Instance["Chart_PriceAxis"];
            plot.Axes.Left.Label.Text = Loc.Instance["Chart_ScoreAxis"];
            plot.ShowLegend(Alignment.LowerRight);
            plot.Axes.AutoScale();
        }

        ScatterPlot.Refresh();
    }

    private void OnScatterMouseMove(object sender, MouseEventArgs e)
    {
        if (_allPoints is null || _points.Count == 0) return;
        var pos = e.GetPosition(ScatterPlot);
        var scale = ScatterPlot.DisplayScale;
        var coords = ScatterPlot.Plot.GetCoordinates(new Pixel((float)(pos.X * scale), (float)(pos.Y * scale)));
        var nearest = _allPoints.Data.GetNearest(coords, ScatterPlot.Plot.LastRender, 24);
        if (!nearest.IsReal)
        {
            HoverText.Text = "";
            return;
        }

        var p = _points[nearest.Index];
        HoverText.Text = $"{p.Name} · {Loc.Instance["Card_Score"]} {Fmt.Score(p.Score)} · {Fmt.Price(p.Price)}/1M";
    }

    private static string Trim(string s) => s.Length <= 24 ? s : s[..23] + "…";
}
