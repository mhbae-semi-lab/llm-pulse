using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMPulse.App.Localization;
using LLMPulse.App.ViewModels;

namespace LLMPulse.App;

/// <summary>개발/검증용: 화면 캡처 없이 WPF 렌더 트리를 PNG 로 저장한다 (--screenshot &lt;dir&gt;).</summary>
internal static class ScreenshotRunner
{
    public static async Task RunAsync(MainWindow window, string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var vm = (MainViewModel)window.DataContext;
        await Task.Delay(1500);

        foreach (var lang in Loc.Languages)
        {
            vm.SelectedLanguage = lang;
            foreach (var nav in vm.NavItems)
            {
                vm.SelectedNav = nav;
                if (nav.Key == PageKey.Metrics) vm.Metrics.Select("ttft_s");
                if (nav.Key == PageKey.Matching && vm.CurrentPage is MatchingViewModel mv) mv.Selected = mv.Items.FirstOrDefault();
                if (nav.Key == PageKey.Plans && vm.CurrentPage is PlansViewModel pv && pv.Cards.FirstOrDefault(c => c.IsSelected) is { } card)
                {
                    card.IsExpanded = true;
                    card.ModelFilter = "";
                }

                await Task.Delay(700);
                Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-{nav.Key}.png"));
            }

            vm.SelectedNav = vm.NavItems.First(n => n.Key == PageKey.Dashboard);
            vm.State.PlanOnly = true;
            await Task.Delay(700);
            Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-DashboardPlanOnly.png"));
            vm.State.Navigate(PageKey.Category, "coding");
            await Task.Delay(1200);
            Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-CategoryPlanOnly.png"));
            vm.State.PlanOnly = false;

            for (var step = 0; step <= WizardViewModel.LastStep; step++)
            {
                if (step == 0) vm.Wizard.Open();
                vm.Wizard.Step = step;
                await Task.Delay(500);
                Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-Wizard{step}.png"));
            }

            vm.Wizard.SkipCommand.Execute(null);
            vm.State.Navigate(PageKey.Category, "coding");
            await Task.Delay(1200);
            Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-Category.png"));
            vm.State.Navigate(PageKey.Category, "my_coding");
            await Task.Delay(1200);
            Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-CategoryMix.png"));
            if (window.FindName("PageHost") is DependencyObject host && FindScroll(host) is { } sv)
            {
                sv.ScrollToEnd();
                await Task.Delay(500);
                Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-CategoryMixWeights.png"));
            }

            vm.State.Navigate(PageKey.Model, "1");
            await Task.Delay(1000);
            Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-Model.png"));
            vm.State.Navigate(PageKey.Compare, "1,2,3,4");
            await Task.Delay(1200);
            Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-Compare.png"));
            if (window.FindName("PageHost") is DependencyObject ch && FindScroll(ch) is { } csv)
            {
                csv.ScrollToVerticalOffset(csv.ScrollableHeight * 0.4);
                await Task.Delay(600);
                Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-CompareDomains.png"));
            }
            var popup = new Views.QuickPopup(vm.Dashboard, () => { }, () => { });
            popup.Show();
            await Task.Delay(700);
            SaveElement((FrameworkElement)popup.Content, System.IO.Path.Combine(dir, $"{lang.Code}-QuickPopup.png"));
            popup.Close();

            vm.SelectedNav = vm.NavItems.First(n => n.Key == PageKey.Dashboard);
            await Task.Delay(1200);
            if (window.FindName("PageHost") is DependencyObject dh && FindScroll(dh) is { } dsv)
            {
                dsv.ScrollToVerticalOffset(dsv.ScrollableHeight * 0.45);
                await Task.Delay(600);
                Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-DashboardScrolled.png"));
                dsv.ScrollToEnd();
                await Task.Delay(600);
                Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-DashboardEnd.png"));
            }

            vm.SelectedNav = vm.NavItems.First(n => n.Key == PageKey.Leaderboard);
            await Task.Delay(900);
            Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-LeaderboardFav.png"));
            if (vm.CurrentPage is LeaderboardViewModel lb)
            {
                // 소스 불일치(⚠) 열 기준 정렬: 매뉴얼의 불일치 설명용
                var (keepKey, keepDesc) = (lb.SortKey, lb.SortDescending);
                lb.SetSort("disagree", true);
                await Task.Delay(700);
                Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-LeaderboardMismatch.png"));
                lb.SetSort(keepKey, keepDesc);
            }

            vm.SelectedNav = vm.NavItems.First(n => n.Key == PageKey.Settings);
            await Task.Delay(900);
            if (window.FindName("PageHost") is DependencyObject sh && FindScroll(sh) is { } ssv)
            {
                ssv.ScrollToVerticalOffset(560); // 트레이 · Claude · 자동 업데이트 · 알림 체크박스
                await Task.Delay(600);
                Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-SettingsMid.png"));
                ssv.ScrollToVerticalOffset(Math.Max(0, ssv.ScrollableHeight - 230)); // 백업 · 소스 신선도 · 소스 상태
                await Task.Delay(600);
                Save(window, System.IO.Path.Combine(dir, $"{lang.Code}-SettingsEnd.png"));
            }
        }

        Application.Current.Shutdown(0);
    }

    private static System.Windows.Controls.ScrollViewer? FindScroll(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer sv) return sv;
            if (FindScroll(child) is { } found) return found;
        }

        return null;
    }

    private static void Save(MainWindow window, string path) => SaveElement((FrameworkElement)window.Content, path);

    private static void SaveElement(FrameworkElement root, string path)
    {
        var w = (int)root.ActualWidth;
        var h = (int)root.ActualHeight;
        if (w == 0 || h == 0) return;

        var bg = (Brush?)Application.Current.TryFindResource("ApplicationBackgroundBrush") ?? Brushes.Black;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, w, h));
        }

        // 매뉴얼용 고해상도: LLMPULSE_SHOT_SCALE=1.5 → 1.5배 선명도로 렌더
        var scale = double.TryParse(Environment.GetEnvironmentVariable("LLMPULSE_SHOT_SCALE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) && sc >= 1 ? sc : 1;
        var bmp = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }
}
