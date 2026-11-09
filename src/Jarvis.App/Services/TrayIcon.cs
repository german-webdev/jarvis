using System.ComponentModel;
using Forms = System.Windows.Forms;

namespace Jarvis.App.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel _runtime;
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _artwork;
    private readonly TrayMenu _menu;
    private readonly Func<bool> _canInteract;
    private readonly Func<bool>? _miniVisible;
    private bool _disposed;

    public TrayIcon(MainViewModel runtime, Action show, Action settings, Action exit, Func<bool> canInteract,
        Action? toggleMini = null, Func<bool>? miniVisible = null)
    {
        _runtime = runtime;
        _canInteract = canInteract;
        _miniVisible = miniVisible;
        using (var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Jarvis;component/Assets/jarvis.ico"))!.Stream)
        using (var icon = new System.Drawing.Icon(resource, 32, 32))
            _artwork = (System.Drawing.Icon)icon.Clone();
        _menu = new TrayMenu(show, runtime.Listen, () => runtime.Stop(), () => { show(); settings(); }, exit, toggleMini);
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
        // Audio level changes are frequent and do not change the tray state.
        if (args?.PropertyName is nameof(MainViewModel.Level)) return;
        _menu.UpdateState(_runtime.PhaseTitle, _runtime.MicrophoneLabel,
            _runtime.CanListen, _runtime.CanStop, _canInteract());
        RefreshMiniState();
        var tooltip = "JARVIS · " + _runtime.MicrophoneLabel.ToLowerInvariant();
        _icon.Text = tooltip.Length <= 127 ? tooltip : tooltip[..127];
    }

    public void RefreshMiniState()
    {
        if (_disposed) return;
        _menu.UpdateMiniState(_miniVisible?.Invoke() ?? false, _canInteract());
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
