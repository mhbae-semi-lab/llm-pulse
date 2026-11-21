using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using LLMPulse.App.ViewModels;

namespace LLMPulse.App.Views;

public partial class MetricsView : UserControl
{
    private MetricsViewModel? _vm;

    public MetricsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.ScrollRequested -= Scroll;
            _vm = DataContext as MetricsViewModel;
            if (_vm is not null) _vm.ScrollRequested += Scroll;
        };
        // ⓘ 로 들어와 뷰가 새로 만들어진 직후에도 선택 항목이 보이도록
        Loaded += (_, _) => { if (_vm?.Selected is { } s) Scroll(s); };
        Unloaded += (_, _) => { if (_vm is not null) _vm.ScrollRequested -= Scroll; };
    }

    private void Scroll(MetricCardViewModel card) =>
        Dispatcher.BeginInvoke(() => List.ScrollIntoView(card), System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        // https 링크만 외부 브라우저로 연다
        if (e.Uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
