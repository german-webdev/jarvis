using System.IO;
using System.Windows;
using System.Text.Json;
using Jarvis.App.Services;

namespace Jarvis.App;

public partial class App : Application
{
    private Mutex? _instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--focus-window") { Shutdown(WindowFocus.Run(e.Args)); return; }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        if (directory == null) { MessageBox.Show("Не найдена папка проекта. Запускай Джарвис через Start-Jarvis.cmd."); Shutdown(1); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--scan-apps")
        {
            var path = Path.GetFullPath(e.Args[1]);
            if (!path.StartsWith(Path.Combine(directory.FullName, "work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { Shutdown(1); return; }
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
        if (!created) { MessageBox.Show("Джарвис уже запущен. Найди его окно на панели задач."); _instance.Dispose(); _instance = null; Shutdown(); return; }
        var window = new MainWindow(directory.FullName, e.Args);
        MainWindow = window; window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.ReleaseMutex(); _instance?.Dispose();
        base.OnExit(e);
    }
}
