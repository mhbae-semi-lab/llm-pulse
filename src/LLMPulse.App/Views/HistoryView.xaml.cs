using System.Windows.Controls;
using LLMPulse.App.Charts;
using LLMPulse.App.ViewModels;
using ScottPlot;

namespace LLMPulse.App.Views;

public partial class HistoryView : UserControl
{
    private HistoryViewModel? _vm;

    public HistoryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.TimelineChanged -= Draw;
            _vm = DataContext as HistoryViewModel;
            if (_vm is not null) _vm.TimelineChanged += Draw;
            Draw();
        };
        Loaded += (_, _) => { ChartTheme.Changed += Draw; Draw(); };
        Unloaded += (_, _) => ChartTheme.Changed -= Draw;
    }

    /// <summary>출시일(가로) 대 종합 점수(세로) 산점도 + 신기록 계단선.</summary>
    private void Draw()
    {
        var plot = TimelinePlot.Plot;
        plot.Clear();
        ChartTheme.Apply(plot);
        var pts = _vm?.ReleasePoints ?? [];
        if (pts.Count > 0)
        {
            double X(DateOnly d) => d.ToDateTime(TimeOnly.MinValue).ToOADate();

            var normal = pts.Where(p => !p.IsRecord).ToList();
            if (normal.Count > 0)
            {
                var s = plot.Add.Scatter(normal.Select(p => X(p.Date)).ToArray(), normal.Select(p => p.Score).ToArray());
                s.LineWidth = 0;
                s.MarkerSize = 8;
                s.Color = ChartTheme.Muted;
            }

            var rec = pts.Where(p => p.IsRecord).ToList();
            var frontier = plot.Add.Scatter(rec.Select(p => X(p.Date)).ToArray(), rec.Select(p => p.Score).ToArray());
            frontier.LineWidth = 2.5f;
            frontier.MarkerSize = 11;
            frontier.Color = ChartTheme.Highlight;
            frontier.ConnectStyle = ConnectStyle.StepHorizontal;

            plot.Axes.DateTimeTicksBottom();
            plot.Axes.AutoScale();
        }

        plot.YLabel("Score");
        TimelinePlot.Refresh();
    }
}