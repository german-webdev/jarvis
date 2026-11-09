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
    private MiniWindow? _mini;
    public MiniWindow? Mini => _mini;

    public MainWindow(string root, string[] args)
    {
        var settings = new Jarvis.Core.SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings;
        Ui.Apply(settings.InterfaceLanguage);
        ThemeManager.Apply(settings.Appearance, settings.ColorStyle);
        InitializeComponent();
        _viewModel = new MainViewModel(root); DataContext = _viewModel;
        LayoutRoot.Children.Remove(HomePage); Shell.Page = HomePage;
        Shell.Navigate = NavigateTo;
        _viewModel.ExitRequested += () => Dispatcher.BeginInvoke(RequestExit);
        _tray = new TrayIcon(_viewModel, ShowFromTray, OpenSettings, RequestExit,
            () => IsEnabled && !_closing && !OwnedWindows.Cast<Window>().Any(window => window.IsVisible)
                && !(_mini?.OwnedWindows.Cast<Window>().Any(window => window.IsVisible) ?? false),
            () => { if (_mini?.IsVisible == true) HideMini(); else ShowMini(); }, () => _mini?.IsVisible == true);
        KeyDown += (_, e) =>
        {
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

    private bool NavigateTo(string section)
    {
        if (section == _section) return true;
        if (Shell.Page is WorkspacePage previous && !previous.ConfirmLeave()) return false;
        if (section == "home") { Shell.Page = HomePage; Shell.Select("home"); _section = "home"; return true; }
        WorkspacePage page = section switch
        {
            "catalog" => new CatalogPage(_viewModel),
            "sites" => new SitesPage(_viewModel),
            "media" => new MediaPage(_viewModel),
            "ai" => new AiConnectionsPage(_viewModel.Ai),
            "settings" => new SettingsPage(_viewModel),
            "help" => new HelpPage(),
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };
        OpenPage(page, section); return true;
    }

    private string _section = "home";
    private void OpenPage(WorkspacePage page, string section)
    {
        page.Navigate = NavigateTo;
        page.CloseRequested += result =>
        {
            if (result == null && !page.ConfirmLeave()) return;
            Shell.Page = HomePage; Shell.Select("home"); _section = "home";
        };
        Shell.Page = page; Shell.Select(section); _section = section;
    }

    private void VoiceControlClick(object sender, RoutedEventArgs e)
        => ToggleVoice();
    private async void RestartWorkerClick(object sender, RoutedEventArgs e) => await _viewModel.RestartAsync();
    private void MessageTextUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
    {
        if (ReferenceEquals(sender, TranscriptText)) TranscriptScroll?.ScrollToTop();
        else if (ReferenceEquals(sender, ReplyText)) ReplyScroll?.ScrollToTop();
    }
    private void ToggleVoice()
    {
        if (_viewModel.CanStop) { _startupListenPending = false; _viewModel.Stop(); }
        else if (_viewModel.CanListen) _viewModel.Listen();
    }
    private void SettingsClick(object sender, RoutedEventArgs e) => OpenSettings();
    private void CatalogClick(object sender, RoutedEventArgs e)
    { NavigateTo("catalog"); }
    private void SitesClick(object sender, RoutedEventArgs e)
    { NavigateTo("sites"); }
    private void MediaClick(object sender, RoutedEventArgs e)
    { NavigateTo("media"); }
    private void AiClick(object sender, RoutedEventArgs e)
    { NavigateTo("ai"); }
    private void TextCommandClick(object sender, RoutedEventArgs e) => OpenTextCommand(this);
    private async void OpenTextCommand(Window owner)
    {
        if (!_viewModel.CanEnterText || _closing || _closed) return;
        var dialog = new TextCommandWindow { Owner = owner, DataContext = _viewModel };
        if (owner is MiniWindow) dialog.ContentRendered += (_, _) => MiniWindow.ConstrainWindow(dialog);
        bool? result;
        try { result = dialog.ShowDialog(); }
        finally { if (owner is MiniWindow mini) mini.ResumeAfterDialog(); }
        if (result == true) await _viewModel.RunTextAsync(dialog.Command);
    }
    private void MiniClick(object sender, RoutedEventArgs e) => ShowMini();
    public void ShowMini()
    {
        if (_closing || _closed || !IsEnabled) return;
        if (_mini == null)
        {
            _mini = new MiniWindow(_viewModel, ToggleVoice, () => OpenTextCommand(_mini!), ShowFromTray, HideMini);
            _mini.IsVisibleChanged += (_, _) => _tray.RefreshMiniState();
        }
        if (!_mini.IsEnabled) return;
        _mini.Show(); Hide();
        _tray.RefreshMiniState();
    }
    public void HideMini()
    {
        if (_mini?.OwnedWindows.Cast<Window>().Any(window => window.IsVisible) == true) return;
        _mini?.Hide(); _tray.RefreshMiniState();
    }
    private void ArtifactClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ArtifactPath is not { } path || !File.Exists(path)) return;
        try { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Ui.Text("Не удалось открыть снимок")); }
    }
    private void OpenSettings()
    {
        ShowFromTray();
        NavigateTo("settings");
    }
    private void ClearHistoryClick(object sender, RoutedEventArgs e) => _viewModel.ClearHistory();
    public void ShowFromTray()
    {
        if (_closing || _closed) return;
        // A modal command belongs to Mini. Restore that dialog rather than hiding its owner.
        if (_mini?.OwnedWindows.Cast<Window>().FirstOrDefault(window => window.IsVisible) is { } miniDialog)
        {
            _mini.Show(); _mini.Activate(); miniDialog.Activate(); return;
        }
        HideMini();
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
        _mini?.DisposeForExit();
        _tray.Dispose();
        // Windows may end the dispatcher immediately; release the worker synchronously.
        try { _viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception error) { Debug.WriteLine(error); }
        _closed = true;
    }
    private void ExitClick(object sender, RoutedEventArgs e) => RequestExit();
    private void HelpClick(object sender, RoutedEventArgs e) => NavigateTo("help");
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
        if (Shell.Page is WorkspacePage page && !page.ConfirmLeave()) { _exitRequested = false; return; }
        _closing = true; IsEnabled = false;
        _mini?.DisposeForExit();
        _tray.Dispose();
        try { await _viewModel.DisposeAsync(); }
        catch (Exception error) { Debug.WriteLine(error); }
        finally { _closed = true; _ = Dispatcher.BeginInvoke(Close); }
    }
}
