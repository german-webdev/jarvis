using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace Jarvis.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closed;
    private bool _closing;

    public MainWindow(string root, string[] args)
    {
        InitializeComponent();
        _viewModel = new MainViewModel(root); DataContext = _viewModel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _viewModel.Stop();
            if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control) OpenSettings();
        };
        Closing += OnClosing;
        var testIndex = Array.IndexOf(args, "--test-audio");
        if (testIndex >= 0 && testIndex + 1 < args.Length)
        {
            _viewModel.Initialized += () =>
            {
                try { _viewModel.ImportTestAudio(args[testIndex + 1]); }
                catch (Exception error) { WriteReport(false, error.Message); }
            };
            _viewModel.CycleCompleted += success => WriteReport(success, _viewModel.Status);
        }
        Loaded += async (_, _) => await _viewModel.RestartAsync();

        void WriteReport(bool success, string status)
        {
            var reportIndex = Array.IndexOf(args, "--report");
            if (reportIndex < 0 || reportIndex + 1 >= args.Length) return;
            var path = Path.GetFullPath(args[reportIndex + 1]);
            if (!path.StartsWith(Path.Combine(root, "work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            File.WriteAllText(path, JsonSerializer.Serialize(new { success, status,
                transcript = _viewModel.Transcript, reply = _viewModel.Reply,
                history = _viewModel.History.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void ListenClick(object sender, RoutedEventArgs e) => _viewModel.Listen();
    private void StopClick(object sender, RoutedEventArgs e) => _viewModel.Stop();
    private void SettingsClick(object sender, RoutedEventArgs e) => OpenSettings();
    private void OpenSettings()
    {
        _viewModel.Stop();
        new SettingsWindow(_viewModel) { Owner = this }.ShowDialog();
    }
    private void ClearHistoryClick(object sender, RoutedEventArgs e) => _viewModel.ClearHistory();
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
    private void HelpClick(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Открой браузер\nЗапусти браузер, пожалуйста\nДжарвис → пауза → открой браузер\n\nСпособ активации, голос и микрофон — в настройках.\nСтоп и Esc выключают прослушивание.", "Команды Джарвиса");
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; IsEnabled = false;
        try { await _viewModel.DisposeAsync(); }
        finally { _closed = true; _ = Dispatcher.BeginInvoke(Close); }
    }
}
