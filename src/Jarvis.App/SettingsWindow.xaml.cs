namespace Jarvis.App;

public sealed class SettingsWindow : WorkspaceDialog
{
    public SettingsWindow(MainViewModel runtime) : base(new SettingsPage(runtime), 730, 780, 620, 660) { SetResourceReference(TitleProperty, "Ui178723ED32B6"); }
}
