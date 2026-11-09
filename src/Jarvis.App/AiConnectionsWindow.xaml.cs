namespace Jarvis.App;

public sealed class AiConnectionsWindow : WorkspaceDialog
{
    public AiConnectionsWindow(Services.AiService service) : base(new AiConnectionsPage(service), 1020, 850, 820, 640) { SetResourceReference(TitleProperty, "Ui46063E22825C"); }
}
