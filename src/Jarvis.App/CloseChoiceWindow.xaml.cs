using System.Windows;
using System.Windows.Input;

namespace Jarvis.App;

public partial class CloseChoiceWindow : Window
{
    public bool ExitRequested { get; private set; }
    public CloseChoiceWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    private void TrayClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void ExitClick(object sender, RoutedEventArgs e) { ExitRequested = true; DialogResult = true; }
}
