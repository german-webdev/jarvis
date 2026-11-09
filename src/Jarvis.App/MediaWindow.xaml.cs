namespace Jarvis.App;

public sealed class MediaWindow : WorkspaceDialog
{
    public MediaWindow(MainViewModel runtime) : base(new MediaPage(runtime), 1100, 780, 900, 640) { }
}
