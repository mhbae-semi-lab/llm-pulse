using System.Diagnostics;
using System.Windows.Navigation;
using LLMPulse.App.ViewModels;
using Wpf.Ui.Controls;

namespace LLMPulse.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        PreviewKeyDown += OnShortcut;
        Loaded += (_, _) => ApplyScale(CurrentScale);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage)) PlayPageTransition();
        };
    }

    /// <summary>페이지가 바뀔 때 살짝 떠오르며 나타나게 한다 (시스템 애니메이션 설정을 따름).</summary>
    private void PlayPageTransition()
    {
        if (!System.Windows.SystemParameters.ClientAreaAnimation) return;

        var move = new System.Windows.Media.TranslateTransform(0, 10);
        PageHost.RenderTransform = move;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(200);
        PageHost.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        move.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(10, 0, dur) { EasingFunction = ease });
    }

    /// <summary>단축키: Ctrl+K 검색, F5 새로고침, Ctrl+P 내 구독만, Ctrl+1~9 페이지 이동.</summary>
    private void OnShortcut(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.Wizard.IsOpen) return;
        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        var key = e.Key;

        if (key == System.Windows.Input.Key.F5 && vm.Refresher.RefreshCommand.CanExecute(null))
        {
            vm.Refresher.RefreshCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && key == System.Windows.Input.Key.K)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && key == System.Windows.Input.Key.P && vm.State.HasPlans)
        {
            vm.State.PlanOnly = !vm.State.PlanOnly;
            e.Handled = true;
        }
        else if (ctrl && key is System.Windows.Input.Key.OemPlus or System.Windows.Input.Key.Add) { StepScale(1); e.Handled = true; }
        else if (ctrl && key is System.Windows.Input.Key.OemMinus or System.Windows.Input.Key.Subtract) { StepScale(-1); e.Handled = true; }
        else if (ctrl && key == System.Windows.Input.Key.D0) { SetScale(1.0); e.Handled = true; }
        else if (ctrl && key is >= System.Windows.Input.Key.D1 and <= System.Windows.Input.Key.D9)
        {
            var i = key - System.Windows.Input.Key.D1;
            if (i < vm.NavItems.Count) vm.SelectedNav = vm.NavItems[i];
            e.Handled = true;
        }
    }

    private const string SettingScale = "ui_scale";
    private static readonly double[] Scales = [0.9, 1.0, 1.15, 1.3];
    private readonly System.Windows.Media.ScaleTransform _scale = new(1, 1);

    private double CurrentScale =>
        DataContext is MainViewModel vm
        && double.TryParse(vm.State.ReadSetting(SettingScale), System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out var s) && Scales.Contains(s) ? s : 1.0;

    private void ApplyScale(double s)
    {
        _scale.ScaleX = _scale.ScaleY = s;
        TopBar.LayoutTransform = _scale;
        BodyGrid.LayoutTransform = _scale;
        FooterText.LayoutTransform = _scale;
    }

    private void SetScale(double s)
    {
        if (DataContext is MainViewModel vm)
            vm.State.SaveSetting(SettingScale, s.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ApplyScale(s);
    }

    private void StepScale(int dir)
    {
        var i = Array.IndexOf(Scales, CurrentScale);
        SetScale(Scales[Math.Clamp(i + dir, 0, Scales.Length - 1)]);
    }

    /// <summary>"⋯" 메뉴: 테마 · PNG 저장 · 글자 크기.</summary>
    private void OnMoreMenu(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var loc = LLMPulse.App.Localization.Loc.Instance;
        var menu = new System.Windows.Controls.ContextMenu { StaysOpen = false };

        var theme = new System.Windows.Controls.MenuItem { Header = loc["Action_Theme"] };
        theme.Click += (_, _) => vm.ToggleThemeCommand.Execute(null);
        menu.Items.Add(theme);

        var png = new System.Windows.Controls.MenuItem { Header = loc["Action_ExportPng"] };
        png.Click += OnExportPng;
        menu.Items.Add(png);

        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = loc["Menu_TextSize"], IsEnabled = false });
        var cur = CurrentScale;
        string[] keys = ["Size_Small", "Size_Normal", "Size_Large", "Size_XLarge"];
        for (var i = 0; i < Scales.Length; i++)
        {
            var s = Scales[i];
            var item = new System.Windows.Controls.MenuItem { Header = loc[keys[i]], IsCheckable = true, IsChecked = s == cur };
            item.Click += (_, _) => SetScale(s);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = (System.Windows.UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnNavItemClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem { DataContext: NavItemViewModel nav } && DataContext is MainViewModel vm)
            vm.ShowNav(nav);
    }

    /// <summary>지금 보이는 페이지를 PNG 로 저장 (스크롤 밖 영역은 포함되지 않음).</summary>
    private void OnExportPng(object sender, System.Windows.RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png",
            FileName = $"llm-pulse-{DateTime.Now:yyyyMMdd-HHmm}.png",
        };
        if (dlg.ShowDialog() != true) return;

        var w = (int)PageHost.ActualWidth;
        var h = (int)PageHost.ActualHeight;
        if (w == 0 || h == 0) return;

        var bg = (System.Windows.Media.Brush?)System.Windows.Application.Current.TryFindResource("ApplicationBackgroundBrush")
                 ?? System.Windows.Media.Brushes.Black;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(bg, null, new System.Windows.Rect(0, 0, w, h));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(PageHost), null, new System.Windows.Rect(0, 0, w, h));
        }

        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(dlg.FileName);
        enc.Save(fs);
    }

    private void OnWizardSaveKey(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.Wizard.SaveApiKey(WizardKeyBox.Password);
        WizardKeyBox.Clear();
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
