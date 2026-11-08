using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Diagnostics;
using Jarvis.App.Services;

namespace Jarvis.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closed;
    private bool _closing;
    private bool _exitRequested;
    private bool _choosingClose;
    private bool _hiddenNoticeShown;
    private bool _startupListenPending = true;
    private readonly TrayIcon _tray;

    public MainWindow(string root, string[] args)
    {
        Ui.Apply(new Jarvis.Core.SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.InterfaceLanguage);
        InitializeComponent();
        _viewModel = new MainViewModel(root); DataContext = _viewModel;
        _viewModel.ExitRequested += () => Dispatcher.BeginInvoke(RequestExit);
        _tray = new TrayIcon(_viewModel, ShowFromTray, OpenSettings, RequestExit, () => IsEnabled && !_closing);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _startupListenPending = false; _viewModel.Stop(); }
            if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control) OpenSettings();
        };
        Closing += OnClosing;
        var testIndex = Array.IndexOf(args, "--test-audio");
        var commandIndex = Array.IndexOf(args, "--test-command");
        if (commandIndex >= 0 && commandIndex + 1 < args.Length)
        {
            _viewModel.Initialized += async () => await _viewModel.RunTextAsync(args[commandIndex + 1]);
            _viewModel.CycleCompleted += success => WriteReport(success, _viewModel.Status);
        }
        if (testIndex >= 0 && testIndex + 1 < args.Length)
        {
            _viewModel.Initialized += () =>
            {
                try { _viewModel.ImportTestAudio(args[testIndex + 1]); }
                catch (Exception error) { WriteReport(false, error.Message); }
            };
            _viewModel.CycleCompleted += success => WriteReport(success, _viewModel.Status);
        }
        var isTest = commandIndex >= 0 || testIndex >= 0;
        var initialReady = true;
        _viewModel.Initialized += () =>
        {
            if (!initialReady) return;
            initialReady = false;
            if (_startupListenPending && !isTest && _viewModel.Settings.ListenOnStartup && _viewModel.CanListen) _viewModel.Listen();
        };
        var initialLoad = true;
        Loaded += async (_, _) =>
        {
            if (!initialLoad) return;
            initialLoad = false;
            if (!isTest && args.Contains("--autostart") && _viewModel.Settings.StartInTray) Hide();
            await _viewModel.RestartAsync();
        };

        void WriteReport(bool success, string status)
        {
            var reportIndex = Array.IndexOf(args, "--report");
            if (reportIndex < 0 || reportIndex + 1 >= args.Length) return;
            var path = Path.GetFullPath(args[reportIndex + 1]);
            if (!path.StartsWith(Path.Combine(root, "work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            File.WriteAllText(path, JsonSerializer.Serialize(new { success, status,
                transcript = _viewModel.Transcript, reply = _viewModel.Reply,
                history = _viewModel.History.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            if (args.Contains("--exit-after-test")) _ = Dispatcher.BeginInvoke(RequestExit);
        }
    }

    private void ListenClick(object sender, RoutedEventArgs e) => _viewModel.Listen();
    private void StopClick(object sender, RoutedEventArgs e) { _startupListenPending = false; _viewModel.Stop(); }
    private void SettingsClick(object sender, RoutedEventArgs e) => OpenSettings();
    private void CatalogClick(object sender, RoutedEventArgs e)
    { _viewModel.Stop(); new CatalogWindow(_viewModel) { Owner = this }.ShowDialog(); }
    private void SitesClick(object sender, RoutedEventArgs e)
    { _viewModel.Stop(); new SitesWindow(_viewModel) { Owner = this }.ShowDialog(); }
    private void AiClick(object sender, RoutedEventArgs e)
    { _startupListenPending = false; _viewModel.Stop(); new AiConnectionsWindow(_viewModel.Ai) { Owner = this }.ShowDialog(); }
    private async void TextCommandClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Stop(keepChoice: true);
        var dialog = new TextCommandWindow { Owner = this };
        if (dialog.ShowDialog() == true) await _viewModel.RunTextAsync(dialog.Command);
    }
    private void ArtifactClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ArtifactPath is not { } path || !File.Exists(path)) return;
        try { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Ui.Text("Не удалось открыть снимок")); }
    }
    private void OpenSettings()
    {
        _startupListenPending = false;
        _viewModel.Stop();
        new SettingsWindow(_viewModel) { Owner = this }.ShowDialog();
    }
    private void ClearHistoryClick(object sender, RoutedEventArgs e) => _viewModel.ClearHistory();
    public void ShowFromTray()
    {
        if (_closing || _closed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        foreach (Window child in OwnedWindows) if (child.IsVisible) child.Activate();
    }
    private void RequestExit()
    {
        if (_choosingClose || _closing) return;
        _exitRequested = true;
        Close();
    }
    public void EndWindowsSession()
    {
        _exitRequested = true;
        _tray.Dispose();
        // Windows may end the dispatcher immediately; release the worker synchronously.
        try { _viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception error) { Debug.WriteLine(error); }
        _closed = true;
    }
    private void ExitClick(object sender, RoutedEventArgs e) => RequestExit();
    private void HelpClick(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing || _choosingClose) return;
        if (!_exitRequested)
        {
            _choosingClose = true;
            var dialog = new CloseChoiceWindow { Owner = this };
            bool? choice;
            try { choice = dialog.ShowDialog(); }
            finally { _choosingClose = false; }
            if (choice != true) return;
            if (!dialog.ExitRequested)
            {
                Hide();
                if (!_hiddenNoticeShown) { _hiddenNoticeShown = true; _tray.ShowHiddenNotice(); }
                return;
            }
        }
        _closing = true; IsEnabled = false;
        _tray.Dispose();
        try { await _viewModel.DisposeAsync(); }
        catch (Exception error) { Debug.WriteLine(error); }
        finally { _closed = true; _ = Dispatcher.BeginInvoke(Close); }
    }
}
