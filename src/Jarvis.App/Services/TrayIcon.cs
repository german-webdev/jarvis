using System.ComponentModel;
using Forms = System.Windows.Forms;

namespace Jarvis.App.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel _runtime;
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _artwork;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.ToolStripMenuItem _listen;
    private readonly Forms.ToolStripMenuItem _stop;
    private readonly Forms.ToolStripMenuItem _settings;
    private readonly Forms.ToolStripMenuItem _exit;
    private readonly Func<bool> _canInteract;
    private bool _disposed;

    public TrayIcon(MainViewModel runtime, Action show, Action settings, Action exit, Func<bool> canInteract)
    {
        _runtime = runtime;
        _canInteract = canInteract;
        using (var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Jarvis;component/Assets/jarvis.ico"))!.Stream)
        using (var icon = new System.Drawing.Icon(resource, 32, 32))
            _artwork = (System.Drawing.Icon)icon.Clone();
        _menu.Items.Add(Ui.Text("Открыть JARVIS"), null, (_, _) => show());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _listen = new(Ui.Text("Начать слушать"), null, (_, _) => runtime.Listen());
        _stop = new(Ui.Text("Остановить прослушивание"), null, (_, _) => runtime.Stop());
        _menu.Items.Add(_listen);
        _menu.Items.Add(_stop);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _settings = new(Ui.Text("Настройки"), null, (_, _) => { show(); settings(); });
        _menu.Items.Add(_settings);
        _exit = new(Ui.Text("Закрыть приложение"), null, (_, _) => exit());
        _menu.Items.Add(_exit);
        _menu.Opening += (_, _) => Update(null, null);
        _icon = new Forms.NotifyIcon
        {
            Icon = _artwork,
            Text = Ui.Text("JARVIS · подготовка"),
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => show();
        runtime.PropertyChanged += Update;
        Update(null, null);
    }

    private void Update(object? sender, PropertyChangedEventArgs? args)
    {
        if (_disposed) return;
        _menu.Items[0].Text = Ui.Text("Открыть JARVIS");
        _listen.Text = Ui.Text("Начать слушать");
        _stop.Text = Ui.Text("Остановить прослушивание");
        _settings.Text = Ui.Text("Настройки");
        _exit.Text = Ui.Text("Закрыть приложение");
        _listen.Enabled = _canInteract() && _runtime.CanListen;
        _stop.Enabled = _runtime.Busy || _runtime.MicrophoneActive;
        _settings.Enabled = _canInteract() && _runtime.CanConfigure;
        _exit.Enabled = _canInteract();
        _icon.Text = "JARVIS · " + _runtime.MicrophoneLabel.ToLowerInvariant();
    }

    public void ShowHiddenNotice() => _icon.ShowBalloonTip(3500, Ui.Text("JARVIS работает в трее"),
        Ui.Text("Двойной щелчок по значку вернёт окно. ") + _runtime.MicrophoneLabel + ".", Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.PropertyChanged -= Update;
        _icon.Visible = false;
        _icon.Dispose();
        _artwork.Dispose();
        _menu.Dispose();
    }
}
