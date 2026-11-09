using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Jarvis.App;

public partial class VideoPanel : UserControl
{
    public VideoPanel() => InitializeComponent();
    private MainViewModel Runtime => (MainViewModel)DataContext;
    private async void ConnectClick(object sender, RoutedEventArgs e) => await Runtime.ConnectVideoBrowserAsync();
    private async void SearchClick(object sender, RoutedEventArgs e) => await Runtime.SearchVideoAsync(QueryBox.Text);
    private async void OpenClick(object sender, RoutedEventArgs e)
    { if (ResultsList.SelectedItem is VideoChoice choice) await Runtime.OpenVideoAsync(choice); }
    private void StopClick(object sender, RoutedEventArgs e) => Runtime.Stop();
    private async void QueryKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await Runtime.SearchVideoAsync(QueryBox.Text); } }
    private async void ResultsKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && ResultsList.SelectedItem is VideoChoice choice) { e.Handled = true; await Runtime.OpenVideoAsync(choice); } }
}
