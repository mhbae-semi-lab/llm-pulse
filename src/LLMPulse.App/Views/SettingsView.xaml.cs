using System.Windows;
using System.Windows.Controls;
using LLMPulse.App.ViewModels;

namespace LLMPulse.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.SaveApiKey(KeyBox.Password);
        KeyBox.Clear(); // 화면/메모리에 평문 키를 남기지 않는다
    }
}
