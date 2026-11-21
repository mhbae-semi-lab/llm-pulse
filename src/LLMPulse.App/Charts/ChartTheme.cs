using ScottPlot;
using Wpf.Ui.Appearance;

namespace LLMPulse.App.Charts;

/// <summary>ScottPlot 을 앱 라이트/다크 테마에 맞춘다. 테마 전환 시 Changed 로 재적용 신호.</summary>
public static class ChartTheme
{
    public static event Action? Changed;

    public static void RaiseChanged() => Changed?.Invoke();

    public static bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    public static Color Text => IsDark ? Color.FromHex("#E6E6E6") : Color.FromHex("#202020");
    public static Color Muted => IsDark ? Color.FromHex("#9A9A9A") : Color.FromHex("#6B6B6B");
    public static Color Grid => IsDark ? Color.FromHex("#3A3A3A") : Color.FromHex("#D9D9D9");

    /// <summary>모델 구분용 팔레트 (색약 고려: 마커 모양/라벨 병행).</summary>
    public static readonly Color[] Series =
    [
        Color.FromHex("#2E8BF0"), Color.FromHex("#F28C28"), Color.FromHex("#3CB371"), Color.FromHex("#C85AD6"),
    ];

    public static Color Accent => Color.FromHex("#2E8BF0");
    /// <summary>내 구독에서 쓸 수 있는 모델 강조색.</summary>
    public static Color Mine => Color.FromHex("#3CB371");
    public static Color Highlight => Color.FromHex("#F2B01E");

    /// <summary>현재 UI 언어에 맞는 글꼴 (ScottPlot 기본 글꼴에는 한/중 글리프가 없어 네모로 보인다).</summary>
    public static string FontName => Localization.Loc.Instance.Culture.TwoLetterISOLanguageName switch
    {
        // ScottPlot 에서 영문 이름 "Malgun Gothic" 은 한글이 네모로 나오고 현지화 이름("맑은 고딕")은 정상이다 (실측)
        "ko" => "맑은 고딕",
        "zh" => "Microsoft YaHei",
        _ => "Segoe UI",
    };

    public static void Apply(Plot plot)
    {
        Fonts.Default = FontName;
        var bg = IsDark ? Color.FromHex("#202020") : Color.FromHex("#F3F3F3");
        var data = IsDark ? Color.FromHex("#262626") : Color.FromHex("#FFFFFF");
        plot.FigureBackground.Color = bg;
        plot.DataBackground.Color = data;
        plot.Axes.Color(Text);
        plot.Grid.MajorLineColor = Grid;
        plot.Legend.BackgroundColor = data;
        plot.Legend.FontColor = Text;
        plot.Legend.OutlineColor = Grid;
        plot.Axes.Bottom.Label.FontSize = 15;
        plot.Axes.Left.Label.FontSize = 15;
        plot.Font.Set(FontName);
        foreach (var axis in new IAxis[] { plot.Axes.Left, plot.Axes.Bottom, plot.Axes.Right, plot.Axes.Top })
        {
            axis.Label.FontName = FontName;
            axis.TickLabelStyle.FontName = FontName;
        }
        plot.Legend.FontName = FontName;
    }
}
