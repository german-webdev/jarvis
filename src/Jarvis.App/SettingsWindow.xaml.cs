using System.Windows;
using System.Windows.Input;

namespace Jarvis.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    public SettingsWindow(MainViewModel runtime)
    {
        InitializeComponent();
        _viewModel = new(runtime); DataContext = _viewModel;
        Closed += (_, _) => runtime.Stop();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) runtime.Stop(); };
    }
    private async void PreviewClick(object sender, RoutedEventArgs e)
    {
        try { await _viewModel.Runtime.TestVoiceAsync(_viewModel.Capture()); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Не удалось воспроизвести голос"); }
    }
    private void StopClick(object sender, RoutedEventArgs e) => _viewModel.Runtime.Stop();
    private void RefreshClick(object sender, RoutedEventArgs e) => _viewModel.Runtime.RefreshDevices();
    private async void RestartClick(object sender, RoutedEventArgs e) => await _viewModel.Runtime.RestartAsync();
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try { _viewModel.Runtime.ApplySettings(_viewModel.Capture()); DialogResult = true; }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Не удалось сохранить настройки"); }
    }
    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
