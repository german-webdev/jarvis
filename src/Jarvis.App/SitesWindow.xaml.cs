namespace Jarvis.App;

public sealed class SitesWindow : WorkspaceDialog
{
    public SitesWindow(MainViewModel runtime) : base(new SitesPage(runtime), 1100, 780, 900, 560) { SetResourceReference(TitleProperty, "Ui786611CBCDA2"); }
}
