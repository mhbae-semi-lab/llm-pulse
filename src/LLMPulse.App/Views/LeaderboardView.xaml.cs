using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LLMPulse.App.Localization;
using LLMPulse.App.ViewModels;

namespace LLMPulse.App.Views;

public partial class LeaderboardView : UserControl
{
    private static readonly HeatBrushConverter Heat = new();
    private static readonly NumberTextConverter NumText = new();
    private static readonly MissingValueTipConverter MissingTip = new();

    private LeaderboardViewModel? _vm;

    public LeaderboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Grid.Sorting += OnSorting;
        Loaded += (_, _) => ApplyPendingSort();
    }

    private void Attach()
    {
        if (_vm is not null) _vm.Rebuilt -= BuildColumns;
        _vm = DataContext as LeaderboardViewModel;
        if (_vm is null) return;
        _vm.Rebuilt += BuildColumns;
        BuildColumns();
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"llm-pulse-leaderboard-{DateTime.Now:yyyyMMdd}.csv",
        };
        if (dlg.ShowDialog() != true) return;
        // Excel 이 UTF-8 로 인식하도록 BOM 포함
        System.IO.File.WriteAllText(dlg.FileName, _vm.BuildCsv(), new System.Text.UTF8Encoding(true));
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm?.SetSelection(Grid.SelectedItems.OfType<LeaderboardRow>().Select(r => r.ModelId));

    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 헤더/빈 영역 더블클릭은 무시하고, 행 위에서만 모델 상세로 이동
        if (e.OriginalSource is DependencyObject d
            && FindAncestor<DataGridRow>(d)?.Item is LeaderboardRow row)
            _vm?.OpenModel(row.ModelId);
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    private void ApplyPendingSort()
    {
        if (_vm?.PendingSortCategoryId is not { } id) return;
        var idx = _vm.Catalog.Categories.ToList().FindIndex(c => c.Id == id);
        _vm.PendingSortCategoryId = null;
        if (idx < 0) return;
        // 낮을수록 좋은 분야(지연/가격)도 분야 점수는 높을수록 좋으므로 항상 내림차순
        _vm.SetSort($"cat:{idx}", descending: true);
        MarkSortArrow();
    }

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // 정렬은 ViewModel 이 담당 (값 없는 행 항상 뒤, 인덱서 경로 정렬 회피)
        if (_vm is null || e.Column.SortMemberPath is not { Length: > 0 } key) return;
        _vm.SetSort(key);
        MarkSortArrow();
    }

    private void MarkSortArrow()
    {
        foreach (var col in Grid.Columns)
        {
            col.SortDirection = col.SortMemberPath == _vm?.SortKey
                ? (_vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    private void BuildColumns()
    {
        if (_vm is null) return;
        var loc = Loc.Instance;
        var lang = loc.Language;
        Grid.Columns.Clear();

        Grid.Columns.Add(StarColumn());
        Grid.Columns.Add(NameColumn(loc["Col_Model"]));
        if (_vm.HasPlans) Grid.Columns.Add(AccessColumn(loc["Col_Access"]));
        if (!_vm.IsColumnHidden("disagree")) Grid.Columns.Add(DisagreeColumn(loc["Col_Disagree"]));
        if (!_vm.IsColumnHidden("vendor")) Grid.Columns.Add(TextColumn(loc["Col_Vendor"], "Vendor", "vendor", 140));

        for (var i = 0; i < _vm.Catalog.Categories.Count; i++)
        {
            var cat = _vm.Catalog.Categories[i];
            if (_vm.IsColumnHidden($"cat:{cat.Id}")) continue;
            Grid.Columns.Add(ScoreColumn(cat.Name.Get(lang), i, cat.Metrics[0].Id));
        }

        if (!_vm.IsColumnHidden("price")) Grid.Columns.Add(NumberColumn(loc["Col_Price"], "Price", "price", "price", "price_blended_per_m"));
        if (!_vm.IsColumnHidden("tok")) Grid.Columns.Add(NumberColumn(loc["Col_Speed"], "TokensPerSecond", "tok", "0.#", "output_tok_s"));
        if (!_vm.IsColumnHidden("ttft")) Grid.Columns.Add(NumberColumn(loc["Col_Ttft"], "Ttft", "ttft", "0.00", "ttft_s"));
        if (!_vm.IsColumnHidden("release")) Grid.Columns.Add(TextColumn(loc["Col_Release"], "Release", "release", 120));
        Grid.FrozenColumnCount = _vm.HasPlans ? 3 : 2;
        MarkSortArrow();
    }

    /// <summary>내 구독에서 사용 가능 여부 배지 (✓ 포함 / ⚠ 제한 / ✕ 불가).</summary>
    private static DataGridTemplateColumn AccessColumn(string header)
    {
        var g = new FrameworkElementFactory(typeof(TextBlock));
        g.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        g.SetValue(TextBlock.FontSizeProperty, 18.0);
        g.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        g.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        g.SetBinding(TextBlock.TextProperty, new Binding("AccessGlyph"));
        g.SetBinding(TextBlock.ForegroundProperty, new Binding("AccessBrush"));
        g.SetBinding(FrameworkElement.ToolTipProperty, new Binding("AccessTip"));
        return new DataGridTemplateColumn
        {
            Header = header,
            SortMemberPath = "access",
            CellTemplate = new DataTemplate { VisualTree = g },
            Width = 90,
        };
    }

    /// <summary>소스 불일치 경고 열 (⚠ + 툴팁에 어떤 값이 어긋났는지). 화면 낭독기는 툴팁 문장을 읽는다.</summary>
    private static DataGridTemplateColumn DisagreeColumn(string header)
    {
        var g = new FrameworkElementFactory(typeof(TextBlock));
        g.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        g.SetValue(TextBlock.FontSizeProperty, 16.0);
        g.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xD9, 0x82, 0x2B)));
        g.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        g.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        g.SetBinding(TextBlock.TextProperty, new Binding("DisagreeGlyph"));
        g.SetBinding(FrameworkElement.ToolTipProperty, new Binding("DisagreeTip"));
        g.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding("DisagreeTip"));
        return new DataGridTemplateColumn
        {
            Header = header,
            SortMemberPath = "disagree",
            CellTemplate = new DataTemplate { VisualTree = g },
            Width = 80,
        };
    }

    /// <summary>즐겨찾기 별 토글 열 (정렬 안 함).</summary>
    private DataGridTemplateColumn StarColumn()
    {
        var star = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton));
        star.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding("IsFavorite") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        star.SetValue(System.Windows.Controls.Primitives.ToggleButton.ToolTipProperty, Loc.Instance["Leaderboard_FavToggle"]);
        star.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        star.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        star.SetValue(Control.PaddingProperty, new Thickness(4, 0, 4, 0));
        star.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        star.SetValue(Control.TemplateProperty, StarTemplate());
        return new DataGridTemplateColumn
        {
            Header = "★",
            CanUserSort = false,
            CellTemplate = new DataTemplate { VisualTree = star },
            Width = 48,
        };
    }

    private static ControlTemplate StarTemplate()
    {
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.Name = "g";
        glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        glyph.SetValue(TextBlock.FontSizeProperty, 18.0);
        glyph.SetValue(TextBlock.TextProperty, "\uE734"); // 빈 별
        glyph.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var tpl = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton)) { VisualTree = glyph };
        var on = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(TextBlock.TextProperty, "\uE735", "g")); // 채운 별
        on.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xF2, 0xB0, 0x1E)), "g"));
        tpl.Triggers.Add(on);
        return tpl;
    }

    /// <summary>"컬럼" 버튼: 보일 열을 체크로 고르고 저장한다.</summary>
    private void OnColumns(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var loc = Loc.Instance;
        var lang = loc.Language;
        var choices = new List<(string Key, string Title)> { ("disagree", loc["Col_Disagree"]), ("vendor", loc["Col_Vendor"]) };
        choices.AddRange(_vm.Catalog.Categories.Select(c => ($"cat:{c.Id}", c.Name.Get(lang))));
        choices.Add(("price", loc["Col_Price"]));
        choices.Add(("tok", loc["Col_Speed"]));
        choices.Add(("ttft", loc["Col_Ttft"]));
        choices.Add(("release", loc["Col_Release"]));

        var menu = new ContextMenu { StaysOpen = false };
        foreach (var (key, title) in choices)
        {
            var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = !_vm.IsColumnHidden(key), StaysOpenOnClick = true };
            var k = key;
            item.Click += (_, _) => _vm.SetColumnHidden(k, !item.IsChecked);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>모델 이름 + 최근 출시(30일) NEW 배지.</summary>
    private DataGridTemplateColumn NameColumn(string header)
    {
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var badgeText = new FrameworkElementFactory(typeof(TextBlock));
        badgeText.SetValue(TextBlock.TextProperty, Loc.Instance["Leaderboard_NewBadge"]);
        badgeText.SetValue(TextBlock.FontSizeProperty, 10.0);
        badgeText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        badgeText.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        var badge = new FrameworkElementFactory(typeof(Border));
        badge.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0xF0)));
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        badge.SetValue(Border.PaddingProperty, new Thickness(5, 1, 5, 1));
        badge.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.SetValue(FrameworkElement.ToolTipProperty, Loc.Instance["Leaderboard_NewTip"]);
        badge.SetBinding(UIElement.VisibilityProperty, new Binding("IsNew") { Converter = new BooleanToVisibilityConverter() });
        badge.AppendChild(badgeText);

        // 이름은 줄임표로 잘리더라도 NEW 배지는 항상 오른쪽에 남도록 열을 나눈다
        var nameCol = new FrameworkElementFactory(typeof(ColumnDefinition));
        nameCol.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var badgeCol = new FrameworkElementFactory(typeof(ColumnDefinition));
        badgeCol.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        var panel = new FrameworkElementFactory(typeof(System.Windows.Controls.Grid));
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0));
        panel.AppendChild(nameCol);
        panel.AppendChild(badgeCol);
        name.SetValue(System.Windows.Controls.Grid.ColumnProperty, 0);
        badge.SetValue(System.Windows.Controls.Grid.ColumnProperty, 1);
        panel.AppendChild(name);
        panel.AppendChild(badge);

        return new DataGridTemplateColumn
        {
            Header = header,
            SortMemberPath = "name",
            CellTemplate = new DataTemplate { VisualTree = panel },
            Width = 320,
        };
    }

    private DataGridTextColumn TextColumn(string header, string path, string key, double width) => new()
    {
        Header = header,
        Binding = new Binding(path),
        SortMemberPath = key,
        Width = width,
    };

    private DataGridTemplateColumn NumberColumn(string header, string path, string key, string format, string metricId)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path) { Converter = NumText, ConverterParameter = format });
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4));

        return new DataGridTemplateColumn
        {
            Header = HeaderWithInfo(header, metricId),
            SortMemberPath = key,
            CellTemplate = new DataTemplate { VisualTree = text },
            MinWidth = 110,
        };
    }

    private DataGridTemplateColumn ScoreColumn(string header, int index, string metricId)
    {
        var path = $"Scores[{index}]";

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path) { Converter = NumText, ConverterParameter = "0.0" });
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding(path) { Converter = Heat });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        // 값이 없을 때만 이유를 알려 주는 툴팁 (최신 모델은 Arena/Aider 반영이 늦다)
        var source = _vm!.Catalog.MetricsById.TryGetValue(metricId, out var def) && def.Source.Length > 0 ? def.Source : header;
        border.SetBinding(FrameworkElement.ToolTipProperty,
            new Binding(path) { Converter = MissingTip, ConverterParameter = Loc.Instance.Format("Leaderboard_NoScoreTip", source) });
        border.AppendChild(text);

        return new DataGridTemplateColumn
        {
            Header = HeaderWithInfo(header, metricId),
            SortMemberPath = $"cat:{index}",
            CellTemplate = new DataTemplate { VisualTree = border },
            MinWidth = 130,
        };
    }

    private object HeaderWithInfo(string title, string metricId)
    {
        var info = new Button
        {
            Content = "\u24D8",
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = Loc.Instance["Action_MetricInfo"],
            Tag = metricId,
            Focusable = true,
        };
        System.Windows.Automation.AutomationProperties.SetName(info, Loc.Instance["Action_MetricInfo"]);
        info.Click += (s, e) =>
        {
            e.Handled = true; // 헤더 정렬 클릭으로 전파되지 않게
            _vm?.OpenMetricInfo((string)((Button)s).Tag);
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(info);
        return panel;
    }
}
