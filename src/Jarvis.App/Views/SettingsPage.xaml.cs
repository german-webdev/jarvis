using System.Windows;
using Jarvis.App.Services;

namespace Jarvis.App;

public partial class SettingsPage : WorkspacePage
{
    private SettingsViewModel _viewModel;
    public SettingsPage(MainViewModel runtime)
    {
        InitializeComponent();
        _viewModel = new(runtime); DataContext = _viewModel;
        Title = Ui.Text("Настройки");
        TrackChanges(() => System.Text.Json.JsonSerializer.Serialize(new {
            Settings = _viewModel.Capture() with { MusicTargetId = null, MiniView = "robot", MiniRobotSize = "medium", MiniRobotScale = null }, _viewModel.StartWithWindows
        }));
        AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new System.Windows.Controls.SelectionChangedEventHandler((_, _) => ClearSavedFeedback()));
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler((_, _) => ClearSavedFeedback()));
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
            new RoutedEventHandler((_, _) => ClearSavedFeedback()));
        AddHandler(System.Windows.Controls.Primitives.RangeBase.ValueChangedEvent,
            new RoutedPropertyChangedEventHandler<double>((_, _) => ClearSavedFeedback()));
    }
    private void ClearSavedFeedback() => Dispatcher.BeginInvoke(() =>
    {
        if (HasPendingChanges) SaveFeedback.Text = "";
    });
    private async void PreviewClick(object sender, RoutedEventArgs e)
    {
        OperationFeedback.Visibility = Visibility.Visible;
        try { await _viewModel.Runtime.TestVoiceAsync(_viewModel.Capture()); }
        catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, Ui.Text("Не удалось воспроизвести голос")); }
    }
    private void StopClick(object sender, RoutedEventArgs e) => _viewModel.Runtime.Stop();
    private void RefreshClick(object sender, RoutedEventArgs e)
    {
        OperationFeedback.Visibility = Visibility.Visible;
        _viewModel.Runtime.RefreshDevices();
    }
    private async void RestartClick(object sender, RoutedEventArgs e)
    {
        OperationFeedback.Visibility = Visibility.Visible;
        await _viewModel.Runtime.RestartAsync();
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var previousStartup = StartupRegistration.ReadCommand();
            var changedStartup = _viewModel.StartWithWindows != (previousStartup != null);
            if (changedStartup) StartupRegistration.SetEnabled(_viewModel.StartWithWindows);
            try { _viewModel.Runtime.ApplySettings(_viewModel.Capture()); }
            catch
            {
                if (changedStartup) StartupRegistration.Restore(previousStartup);
                throw;
            }
            // Keep the page, selected tab and scroll position; refresh labels after a language change.
            _viewModel = new SettingsViewModel(_viewModel.Runtime);
            DataContext = _viewModel;
            AcceptChanges();
            OperationFeedback.Visibility = Visibility.Collapsed;
            SaveFeedback.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, Ui.Key("Настройки сохранены"));
        }
        catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, Ui.Text("Не удалось сохранить настройки")); }
    }
    private void CancelClick(object sender, RoutedEventArgs e) => Finish(false);
}
