namespace Jarvis.App;

public sealed class CatalogWindow : WorkspaceDialog
{
    public CatalogWindow(MainViewModel runtime) : base(new CatalogPage(runtime), 1200, 740, 900, 600) { SetResourceReference(TitleProperty, "UiEEAAD935F360"); }
}
