using System.IO;
using System.Windows;

namespace Jarvis.App;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, "Local\\JarvisVoicePrototype", out var created);
        if (!created) { MessageBox.Show("Джарвис уже запущен. Найди его окно на панели задач."); _instance.Dispose(); _instance = null; Shutdown(); return; }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        if (directory == null) { MessageBox.Show("Не найдена папка проекта. Запускай Джарвис через Start-Jarvis.cmd."); Shutdown(1); return; }
        var window = new MainWindow(directory.FullName, e.Args);
        MainWindow = window; window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.ReleaseMutex(); _instance?.Dispose();
        base.OnExit(e);
    }
}
