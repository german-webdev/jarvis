using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Jarvis.App;
using Xunit;

namespace Jarvis.App.Tests;

public class WindowLifecycleTests
{
    [Fact]
    public void CloseDialogCancelTrayRestoreAndExitPreserveTheirMeaning()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var root = Path.Combine(Path.GetTempPath(), "jarvis-shell-test-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            MainWindow? window = null;
            application.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    // Empty fixture: no user catalog, speech model, microphone recording or startup registration.
                    window = new MainWindow(root, []) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                    var closed = false;
                    window.Closed += (_, _) => closed = true;
                    window.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var runtime = Assert.IsType<MainViewModel>(window.DataContext);
                    Assert.False(runtime.MicrophoneActive);

                    runtime.ApplySettings(runtime.Settings with { InterfaceLanguage = "en" });
                    Assert.Equal("en", new Jarvis.Core.SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.InterfaceLanguage);
                    Assert.Equal("en", new SettingsViewModel(runtime).Capture().InterfaceLanguage);
                    Assert.Equal("JARVIS · Assistant", window.Title);
                    Assert.Contains(Descendants(window).OfType<MenuItem>(), item => Equals(item.Header, "Exit"));
                    Assert.Equal("Microphone off", runtime.MicrophoneLabel);
                    Assert.Equal("Heard the name. Waiting up to 8 seconds for a command…", Ui.Text("Имя услышано. Жду команду до 8 секунд…"));
                    Assert.Equal("12:34:56  Text command: запусти Word", Ui.Text("12:34:56  Команда текстом: запусти Word"));
                    Assert.All(Ui.Translations, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
                    var englishSettings = new SettingsWindow(runtime);
                    Assert.Equal("Settings · JARVIS", englishSettings.Title);
                    Assert.Contains(Descendants(englishSettings).OfType<TextBlock>(), text => text.Text == "Interface language");
                    englishSettings.Close();
                    var englishSites = new SitesWindow(runtime);
                    Assert.Equal("Websites · JARVIS", englishSites.Title);
                    Assert.Contains(Descendants(englishSites).OfType<DataGrid>().Single().Columns, column => Equals(column.Header, "Website address"));
                    englishSites.Close();
                    var englishClose = new CloseChoiceWindow();
                    Assert.Equal(new[] { "Minimize to tray", "Exit application" }, Descendants(englishClose).OfType<Button>().Select(button => button.Content));
                    englishClose.Close();
                    runtime.ApplySettings(runtime.Settings with { InterfaceLanguage = "ru" });
                    Assert.Equal("JARVIS · Помощник", window.Title);

                    void ChooseClose(Action<CloseChoiceWindow> choose)
                    {
                        window.Dispatcher.BeginInvoke(() =>
                        {
                            var dialog = Assert.Single(window.OwnedWindows.OfType<CloseChoiceWindow>());
                            var buttons = Descendants(dialog).OfType<Button>().ToArray();
                            Assert.Equal(new[] { "Убрать в трей", "Закрыть приложение" }, buttons.Select(b => b.Content));
                            choose(dialog);
                        });
                        window.Close();
                    }

                    ChooseClose(dialog => dialog.Close());
                    Assert.True(window.IsVisible);
                    Assert.False(closed);
                    ChooseClose(dialog => Descendants(dialog).OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                    Assert.False(window.IsVisible);
                    Assert.False(closed);
                    window.ShowFromTray();
                    Assert.True(window.IsVisible);
                    Assert.Same(runtime, window.DataContext);
                    ChooseClose(dialog => Descendants(dialog).OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(closed);
                    Assert.False(runtime.MicrophoneActive);

                    window = new MainWindow(root, ["--autostart"]) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                    closed = false;
                    window.Closed += (_, _) => closed = true;
                    window.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.False(window.IsVisible);
                    Assert.False(((MainViewModel)window.DataContext).MicrophoneActive);
                    window.ShowFromTray();
                    Descendants(window).OfType<MenuItem>().Single(item => Equals(item.Header, "Выход"))
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(closed);

                    window = new MainWindow(root, []) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                    closed = false;
                    window.Closed += (_, _) => closed = true;
                    window.Show();
                    window.EndWindowsSession();
                    window.Close();
                    Assert.True(closed);
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    window?.EndWindowsSession();
                    application.Shutdown();
                    File.Delete(Path.Combine(root, "data", "settings.json"));
                    if (Directory.Exists(Path.Combine(root, "data"))) Directory.Delete(Path.Combine(root, "data"));
                    Directory.Delete(root);
                }
            });
            application.DispatcherUnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; application.Shutdown(); };
            application.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Window lifecycle test timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
