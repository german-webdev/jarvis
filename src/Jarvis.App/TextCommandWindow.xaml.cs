using System.Windows;
namespace Jarvis.App;
public partial class TextCommandWindow : Window
{
    public string Command => CommandBox.Text;
    public TextCommandWindow() { InitializeComponent(); Loaded += (_, _) => CommandBox.Focus(); }
    private void RunClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel runtime && !runtime.CanSpeak && !Jarvis.Core.AiRouting.IsStopTask(Command)) return;
        if (!string.IsNullOrWhiteSpace(Command)) DialogResult = true;
    }
}
