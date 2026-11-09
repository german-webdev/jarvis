using System.Windows;
using System.Windows.Controls;

namespace Jarvis.App;

/// <summary>A workspace section, reusable inside the main shell or a short-lived dialog.</summary>
public class WorkspacePage : UserControl
{
    public string Title { get; protected set; } = "JARVIS";
    public event Action<bool?>? CloseRequested;
    public Func<string, bool>? Navigate { get; set; }
    private Func<string>? _snapshot;
    private string? _initialSnapshot;
    protected void TrackChanges(Func<string> snapshot) { _snapshot = snapshot; _initialSnapshot = snapshot(); }
    protected void AcceptChanges() { if (_snapshot != null) _initialSnapshot = _snapshot(); }
    public virtual bool HasPendingChanges
    {
        get { try { return _snapshot != null && _snapshot() != _initialSnapshot; } catch { return true; } }
    }
    protected void Finish(bool? result = null) => CloseRequested?.Invoke(result);

    public bool ConfirmLeave() => !HasPendingChanges || MessageBox.Show(Window.GetWindow(this),
        Ui.Text("В этом разделе есть несохранённые изменения. Выйти без сохранения?"),
        Ui.Text("Несохранённые изменения"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

/// <summary>Compatibility host for callers that still need a modal section.</summary>
public class WorkspaceDialog : Window
{
    public new object FindName(string name) => (base.FindName(name) ?? (Content as WorkspacePage)?.FindName(name))!;
    public WorkspaceDialog(WorkspacePage page, double width, double height, double minWidth, double minHeight)
    {
        Content = page; Title = page.Title;
        Width = width; Height = height; MinWidth = minWidth; MinHeight = minHeight;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "ThemeWindow");
        SetResourceReference(ForegroundProperty, "ThemeText");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var finishing = false;
        page.CloseRequested += result =>
        {
            if (result == null && !page.ConfirmLeave()) return;
            finishing = true;
            if (result.HasValue) DialogResult = result; else Close();
        };
        Closing += (_, e) => { if (IsLoaded && !finishing && !page.ConfirmLeave()) e.Cancel = true; };
    }
}
