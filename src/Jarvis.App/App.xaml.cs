using System.IO;
using System.Windows;
using System.Text.Json;
using Jarvis.App.Services;

namespace Jarvis.App;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showWindow;
    private RegisteredWaitHandle? _showWait;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--focus-window") { Shutdown(WindowFocus.Run(e.Args)); return; }
        Jarvis.Core.InstallationLayout layout;
        try { layout = Jarvis.Core.InstallationLayout.Resolve(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); }
        catch (Exception ex) { MessageBox.Show(Ui.Text(ex.Message)); Shutdown(1); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--check-installation")
        {
            Shutdown(await InstallationCheck.RunAsync(layout, e.Args[1])); return;
        }
        Directory.CreateDirectory(layout.UserRoot);
        if (e.Args.Length == 2 && e.Args[0] == "--scan-apps")
        {
            var path = Path.GetFullPath(e.Args[1]);
            if (!path.StartsWith(Path.Combine(layout.UserRoot, "work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { Shutdown(1); return; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var result = await AppDiscovery.ScanAsync().WaitAsync(TimeSpan.FromSeconds(30));
                File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown();
            }
            catch { Shutdown(1); }
            return;
        }
        _instance = new Mutex(true, "Local\\JarvisVoicePrototype", out var created);
        if (!created)
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\JarvisVoicePrototype.Show");
            signal.Set();
            _instance.Dispose(); _instance = null; Shutdown(); return;
        }
        _showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\JarvisVoicePrototype.Show");
        var window = new MainWindow(layout.UserRoot, e.Args);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showWindow, (_, _) =>
            Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);
        MainWindow = window; window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window) window.EndWindowsSession();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showWindow?.Dispose();
        _instance?.ReleaseMutex(); _instance?.Dispose();
        base.OnExit(e);
    }
}
