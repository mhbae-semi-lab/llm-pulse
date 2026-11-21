using System.Windows;
using LLMPulse.App.ViewModels;

namespace LLMPulse.App.Views;

/// <summary>트레이 아이콘을 눌렀을 때 뜨는 작은 요약 창: 오늘의 코딩 추천과 변경 요약.</summary>
public partial class QuickPopup : Window
{
    private readonly Action _open;
    private readonly Action _refresh;

    private readonly bool _pinned;

    /// <param name="pinned">true 면 항상 위에 고정된 미니 위젯 (포커스를 잃어도 숨지 않고, 끌어서 옮길 수 있음).</param>
    public QuickPopup(DashboardViewModel vm, Action open, Action refresh, bool pinned = false)
    {
        _pinned = pinned;
        DataContext = vm;
        _open = open;
        _refresh = refresh;
        InitializeComponent();
        CloseButton.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        SourceInitialized += (_, _) => PlaceNearTray();
    }

    /// <summary>작업 표시줄 쪽(보통 오른쪽 아래) 가장자리에 붙인다.</summary>
    public void PlaceNearTray()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - (ActualHeight > 0 ? ActualHeight : 260) - 12;
    }

    private void OnDeactivated(object? sender, EventArgs e) { if (!_pinned) Hide(); }

    private void OnDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_pinned && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnOpen(object sender, RoutedEventArgs e) { if (!_pinned) Hide(); _open(); }

    private void OnRefresh(object sender, RoutedEventArgs e) { if (!_pinned) Hide(); _refresh(); }
}