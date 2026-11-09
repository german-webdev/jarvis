namespace Jarvis.App;

public sealed class HelpWindow : WorkspaceDialog
{
    public HelpWindow() : base(new HelpPage(), 1040, 780, 780, 580) { SetResourceReference(TitleProperty, "Ui0B64F8071784"); }
}
