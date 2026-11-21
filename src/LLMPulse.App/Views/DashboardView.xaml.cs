using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LLMPulse.App.ViewModels;

namespace LLMPulse.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    /// <summary>카드가 나타날 때 아래에서 살짝 떠오르며 페이드인 (카드마다 조금씩 지연). 시스템 애니메이션이 꺼져 있으면 생략.</summary>
    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border card || !SystemParameters.ClientAreaAnimation) return;

        var delay = TimeSpan.FromMilliseconds(Math.Min(((card.DataContext as CategoryCardViewModel)?.Index ?? 0) * 45, 450));
        var move = new TranslateTransform(0, 14);
        card.RenderTransform = move;
        card.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(260);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { BeginTime = delay, EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, dur) { BeginTime = delay, EasingFunction = ease });
    }
}