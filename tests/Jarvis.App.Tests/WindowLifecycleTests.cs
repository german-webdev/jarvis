using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Jarvis.App;
using Jarvis.Core;
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
                    Assert.Equal("Здесь появится ответ JARVIS", runtime.Reply);
                    Assert.False(runtime.Ai.Configuration.Enabled);
                    Assert.Empty(runtime.Ai.Configuration.Connections);
                    Assert.False(File.Exists(Path.Combine(root, "data", "ai-connections.json")));
                    Assert.False(Directory.Exists(Path.Combine(root, "data", "ai-sessions")));
                    Assert.False((await runtime.Ai.AskAsync("тест", CancellationToken.None)).Verified);

                    runtime.ApplySettings(runtime.Settings with { InterfaceLanguage = "en" });
                    Assert.Equal("en", new Jarvis.Core.SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.InterfaceLanguage);
                    Assert.Equal("en", new SettingsViewModel(runtime).Capture().InterfaceLanguage);
                    Assert.Equal("JARVIS", window.Title);
                    Assert.DoesNotContain(Descendants(window).OfType<Button>(), item => Equals(item.Content, "Exit"));
                    Assert.Equal("JARVIS is not listening", runtime.MicrophoneLabel);
                    Assert.Equal("JARVIS responses will appear here", runtime.Reply);
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
                    var aiWindow = new AiConnectionsWindow(runtime.Ai);
                    Assert.Equal("AI connections · JARVIS", aiWindow.Title);
                    Assert.False(((CheckBox)aiWindow.FindName("EnabledBox")).IsChecked);
                    Assert.Empty(((ListBox)aiWindow.FindName("ConnectionsList")).Items);
                    Descendants(aiWindow).OfType<Button>().Single(button => Equals(button.Content, "Add")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ((TextBox)aiWindow.FindName("NameBox")).Text = "Fixture API";
                    ((TextBox)aiWindow.FindName("EndpointBox")).Text = "https://example.test/v1";
                    ((ComboBox)aiWindow.FindName("ModelBox")).Text = "fixture-model";
                    ((PasswordBox)aiWindow.FindName("KeyBox")).Password = "fixture-not-a-real-key";
                    ((Button)aiWindow.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var savedConnection = Assert.Single(runtime.Ai.Configuration.Connections);
                    Assert.False(runtime.Ai.Configuration.Enabled);
                    Assert.Null(runtime.Ai.Configuration.Active);
                    Assert.DoesNotContain("fixture-not-a-real-key", File.ReadAllText(Path.Combine(root, "data", "ai-connections.json")));
                    Assert.NotEmpty(savedConnection.ProtectedApiKey);
                    ((TextBox)aiWindow.FindName("EndpointBox")).Text = "https://different.example.test/v1";
                    ((Button)aiWindow.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Empty(Assert.Single(runtime.Ai.Configuration.Connections).ProtectedApiKey);
                    ((CheckBox)aiWindow.FindName("EnabledBox")).IsChecked = true;
                    ((ComboBox)aiWindow.FindName("ActiveBox")).SelectedValue = savedConnection.Id;
                    ((Button)aiWindow.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.NotNull(runtime.Ai.Configuration.Active);
                    ((CheckBox)aiWindow.FindName("EnabledBox")).IsChecked = false;
                    ((Button)aiWindow.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Null(runtime.Ai.Configuration.Active);
                    Assert.False(Directory.Exists(Path.Combine(root, "data", "ai-sessions")));
                    Assert.Null(aiWindow.FindName("CliBox"));
                    ((ComboBox)aiWindow.FindName("ProviderBox")).SelectedValue = "chatgpt";
                    Assert.Equal("Continue with ChatGPT", ((Button)aiWindow.FindName("SignInButton")).Content);
                    Assert.Equal(Visibility.Visible, ((Button)aiWindow.FindName("ModelsButton")).Visibility);
                    Assert.False(Directory.Exists(Path.Combine(root, "data", "chatgpt-accounts")));
                    aiWindow.Close();
                    var shell = Assert.IsType<Jarvis.App.Controls.WorkspaceShell>(window.FindName("Shell"));
                    Assert.Null(window.FindName("NewConversationButton"));
                    var remainingNavigation = Assert.IsType<ListBox>(shell.FindName("Navigation"));
                    Assert.Equal(new[] { "home", "catalog", "sites", "media", "ai", "settings", "help" },
                        remainingNavigation.Items.Cast<Jarvis.App.Controls.WorkspaceShell.Section>().Select(section => section.Id));
                    var textCommandButton = Assert.IsType<Button>(window.FindName("TextCommandButton"));
                    var miniJarvisButton = Assert.IsType<Button>(window.FindName("MiniJarvisButton"));
                    Assert.True(miniJarvisButton.IsEnabled);
                    Assert.Equal("Show Mini JARVIS", System.Windows.Automation.AutomationProperties.GetName(miniJarvisButton));
                    Assert.False(string.IsNullOrWhiteSpace(miniJarvisButton.ToolTip?.ToString()));
                    // Simulate an active session without opening any capture device or speech worker.
                    // Stop() would cancel this token and reset the state, exposing incidental UI stops.
                    var privateFields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    await VerifyLongReplyRecovery(runtime, window);
                    await LocalActionViewModelChecks.VerifyAsync(root, window);
                    await TaskVoiceInterruptViewModelChecks.VerifyAsync(root);
                    await BrowserVideoViewModelChecks.VerifyAsync(root);
                    var operation = (CancellationTokenSource)typeof(MainViewModel).GetField("_operation", privateFields)!.GetValue(runtime)!;
                    var listeningToken = operation.Token;
                    var sessionBeforeNavigation = typeof(MainViewModel).GetField("_session", privateFields)!.GetValue(runtime);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(runtime, true);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.MicrophoneActive))!.SetValue(runtime, true);
                    typeof(MainViewModel).GetField("_acceptTranscript", privateFields)!.SetValue(runtime, true);
                    typeof(MainViewModel).GetField("_resumeWake", privateFields)!.SetValue(runtime, true);
                    var pendingPower = (Jarvis.Core.PowerConfirmation)typeof(MainViewModel).GetField("_powerConfirmation", privateFields)!.GetValue(runtime)!;
                    pendingPower.Begin(Jarvis.Core.Intent.SleepComputer, DateTimeOffset.UtcNow);
                    void AssertListeningUnchanged()
                    {
                        Assert.True(runtime.Busy);
                        Assert.True(runtime.MicrophoneActive);
                        Assert.False(listeningToken.IsCancellationRequested);
                        Assert.True(pendingPower.HasPending);
                        Assert.Equal(sessionBeforeNavigation, typeof(MainViewModel).GetField("_session", privateFields)!.GetValue(runtime));
                        Assert.Equal(true, typeof(MainViewModel).GetField("_acceptTranscript", privateFields)!.GetValue(runtime));
                        Assert.Equal(true, typeof(MainViewModel).GetField("_resumeWake", privateFields)!.GetValue(runtime));
                    }
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.False(textCommandButton.IsEnabled);
                    // Mini is another view of the current session. Switching windows, hiding it,
                    // or closing its surface must not cancel listening or a pending confirmation.
                    miniJarvisButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var mini = Assert.IsType<MiniWindow>(window.Mini);
                    mini.Left = -10000; mini.Top = -10000;
                    Assert.Same(runtime, mini.DataContext);
                    Assert.Null(mini.Owner);
                    Assert.True(mini.IsVisible);
                    Assert.False(window.IsVisible);
                    AssertListeningUnchanged();
                    var miniSurface = Assert.IsType<Grid>(mini.FindName("MiniSurface"));
                    var actionStrip = Assert.IsType<StackPanel>(mini.FindName("ActionStrip"));
                    var miniTextButton = Assert.IsType<Button>(mini.FindName("MiniTextButton"));
                    var miniVoiceButton = Assert.IsType<Button>(mini.FindName("MiniVoiceButton"));
                    var miniOpenButton = Assert.IsType<Button>(mini.FindName("MiniOpenButton"));
                    Assert.Null(mini.FindName("MiniMenuButton"));
                    Assert.Equal(new[] { miniTextButton, miniVoiceButton, miniOpenButton }, actionStrip.Children.OfType<Button>());
                    Assert.DoesNotContain(Descendants(mini).OfType<TextBlock>(), text => !string.IsNullOrWhiteSpace(text.Text));
                    mini.CollapseControls();
                    Assert.False(mini.ControlsExpanded);
                    Assert.False(actionStrip.IsVisible);
                    Assert.False(actionStrip.IsHitTestVisible);
                    miniSurface.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
                    Assert.True(mini.ControlsExpanded);
                    Assert.True(actionStrip.IsVisible);
                    Assert.True(actionStrip.IsHitTestVisible);
                    miniSurface.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
                    // Collapse starts in this input event, without an artificial delay.
                    Assert.False(mini.ControlsExpanded);
                    Assert.False(actionStrip.IsVisible);
                    Assert.False(actionStrip.IsHitTestVisible);
                    AssertListeningUnchanged();
                    mini.RevealControls();
                    mini.UpdateLayout();
                    Assert.Equal(miniVoiceButton.ActualWidth, miniTextButton.ActualWidth);
                    Assert.Equal(miniVoiceButton.ActualWidth, miniOpenButton.ActualWidth);
                    Assert.Equal(miniVoiceButton.ActualHeight, miniTextButton.ActualHeight);
                    Assert.Equal(miniVoiceButton.ActualHeight, miniOpenButton.ActualHeight);
                    var textCenter = miniTextButton.TranslatePoint(new Point(miniTextButton.ActualWidth / 2, 0), actionStrip).X;
                    var voiceCenter = miniVoiceButton.TranslatePoint(new Point(miniVoiceButton.ActualWidth / 2, 0), actionStrip).X;
                    var openCenter = miniOpenButton.TranslatePoint(new Point(miniOpenButton.ActualWidth / 2, 0), actionStrip).X;
                    Assert.Equal(voiceCenter - textCenter, openCenter - voiceCenter, 3);
                    Assert.Equal(actionStrip.ActualWidth / 2, voiceCenter, 3);
                    Assert.False(miniTextButton.IsEnabled);
                    Assert.True(miniVoiceButton.IsEnabled);
                    Assert.Equal("Stop", System.Windows.Automation.AutomationProperties.GetName(miniVoiceButton));
                    // A queued text click while busy must not open a command dialog.
                    miniTextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Empty(mini.OwnedWindows.Cast<Window>());
                    AssertListeningUnchanged();
                    window.HideMini();
                    Assert.False(mini.IsVisible);
                    Assert.False(mini.ControlsExpanded);
                    AssertListeningUnchanged();
                    window.ShowMini();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Same(mini, window.Mini);
                    Assert.True(mini.IsVisible);
                    AssertListeningUnchanged();
                    mini.Close();
                    Assert.False(mini.IsVisible);
                    AssertListeningUnchanged();
                    window.ShowMini();
                    Assert.Same(mini, window.Mini);
                    Assert.True(mini.IsVisible);
                    window.ShowFromTray();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(window.IsVisible);
                    Assert.False(mini.IsVisible);
                    Assert.Same(runtime, window.DataContext);
                    AssertListeningUnchanged();
                    window.ShowMini();
                    mini.RevealControls();
                    miniOpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(window.IsVisible);
                    Assert.False(mini.IsVisible);
                    AssertListeningUnchanged();
                    foreach (var section in new[] { "catalog", "sites", "ai", "help", "settings", "home" })
                    {
                        Assert.True(shell.Navigate!(section));
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        AssertListeningUnchanged();
                    }
                    // Media uses the same page lifecycle, with session observation disabled in the fixture.
                    var mediaPage = new MediaPage(runtime, monitorPlayback: false);
                    typeof(MainWindow).GetMethod("OpenPage", privateFields)!.Invoke(window, [mediaPage, "media"]);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    AssertListeningUnchanged();
                    Assert.True(shell.Navigate!("settings"));
                    var liveSettings = Assert.IsType<SettingsPage>(shell.Page);
                    var liveTabs = (TabControl)liveSettings.FindName("SettingsTabs");
                    var liveApplicationTab = liveSettings.FindName("ApplicationTab");
                    liveTabs.SelectedItem = liveApplicationTab;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    ((ComboBox)liveSettings.FindName("AppearanceBox")).SelectedValue = "light";
                    var liveStyleBox = Assert.IsType<ComboBox>(liveSettings.FindName("ColorStyleBox"));
                    Assert.True(liveStyleBox.IsEnabled);
                    liveStyleBox.SelectedValue = "sapphire";
                    var livePitchSlider = Assert.IsType<Slider>(liveSettings.FindName("PitchSlider"));
                    Assert.Equal(-6d, livePitchSlider.Minimum);
                    Assert.Equal(6d, livePitchSlider.Maximum);
                    Assert.Equal(0d, livePitchSlider.Value);
                    livePitchSlider.Value = 2.5;
                    Assert.Equal(2.5, Assert.IsType<SettingsViewModel>(liveSettings.DataContext).Capture().VoicePitch);
                    Assert.Equal(0d, runtime.Settings.VoicePitch);
                    Assert.True(liveSettings.HasPendingChanges);
                    var draftPreferences = liveSettings.DataContext;
                    // Mini preferences belong to the shared runtime. Live changes must retain an
                    // existing general-settings draft, its selected tab, and the listening session.
                    window.ShowMini();
                    var miniMenu = Assert.IsType<ContextMenu>(typeof(MiniWindow).GetField("_menu", privateFields)!.GetValue(mini));
                    Assert.Equal(2, miniMenu.Items.Count);
                    var miniSettingsItem = Assert.IsType<MenuItem>(miniMenu.Items[0]);
                    var robotSurface = Assert.IsType<Border>(mini.FindName("RobotSurface"));
                    Exception? miniPreferencesFailure = null;
                    var openedMiniPreferences = false;
                    var miniSettingsFile = Path.Combine(root, "data", "settings.json");
                    var beforeMiniDialog = File.ReadAllText(miniSettingsFile);
                    var beforeMiniDialogWrite = File.GetLastWriteTimeUtc(miniSettingsFile);
                    _ = mini.Dispatcher.BeginInvoke(async () =>
                    {
                        MiniSettingsWindow? dialog = null;
                        try
                        {
                            dialog = Assert.Single(mini.OwnedWindows.OfType<MiniSettingsWindow>());
                            dialog.Left = -10000; dialog.Top = -10000;
                            Assert.True(dialog.IsVisible);
                            var viewBox = Assert.IsType<ComboBox>(dialog.FindName("MiniViewBox"));
                            var scaleSlider = Assert.IsType<Slider>(dialog.FindName("RobotScaleSlider"));
                            Assert.Null(dialog.FindName("RobotSizeBox"));
                            Assert.Null(dialog.FindName("SaveMiniSettingsButton"));
                            Assert.Null(dialog.FindName("CancelMiniSettingsButton"));
                            Assert.Equal(new[] { "robot", "panel" }, viewBox.Items.Cast<Choice>().Select(choice => choice.Id));
                            Assert.Equal("robot", viewBox.SelectedValue);
                            Assert.Equal(60d, scaleSlider.Minimum);
                            Assert.Equal(160d, scaleSlider.Maximum);
                            Assert.Equal(100d, scaleSlider.Value);
                            Assert.False(scaleSlider.IsSnapToTickEnabled);
                            // Opening and rebuilding translated choices must not schedule a settings write.
                            Ui.Apply("ru");
                            Ui.Apply("en");
                            await Task.Delay(350);
                            Assert.Equal(beforeMiniDialog, File.ReadAllText(miniSettingsFile));
                            Assert.Equal(beforeMiniDialogWrite, File.GetLastWriteTimeUtc(miniSettingsFile));
                            Assert.Equal(100d, scaleSlider.Value);
                            Assert.Equal("robot", viewBox.SelectedValue);
                            var platform = Assert.IsType<Border>(mini.FindName("Platform"));
                            mini.RevealControls(); mini.UpdateLayout();
                            var platformAnchor = platform.PointToScreen(new Point(platform.ActualWidth / 2, 0));
                            var feetAnchor = robotSurface.PointToScreen(new Point(robotSurface.ActualWidth / 2, robotSurface.ActualHeight * .97));
                            var windowBounds = new Rect(mini.Left, mini.Top, mini.Width, mini.Height);
                            void AssertResizeAnchor()
                            {
                                mini.UpdateLayout();
                                Assert.Equal(windowBounds, new Rect(mini.Left, mini.Top, mini.Width, mini.Height));
                                var platformPoint = platform.PointToScreen(new Point(platform.ActualWidth / 2, 0));
                                var feetPoint = robotSurface.PointToScreen(new Point(robotSurface.ActualWidth / 2, robotSurface.ActualHeight * .97));
                                Assert.InRange((platformPoint - platformAnchor).Length, 0, 0.001);
                                Assert.InRange((feetPoint - feetAnchor).Length, 0, 0.001);
                            }
                            foreach (var scale in new[] { 0.93, 1.17, 1.6 })
                            {
                                var beforePreview = File.ReadAllText(miniSettingsFile);
                                scaleSlider.Value = scale * 100;
                                Assert.Equal(scale, runtime.Settings.MiniRobotScale!.Value, 6);
                                Assert.Equal(beforePreview, File.ReadAllText(miniSettingsFile));
                                AssertResizeAnchor(); // No transient move before the dispatcher settles.
                                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                                AssertResizeAnchor();
                                Assert.True(dialog.IsVisible);
                                await Task.Delay(150); // Let the short visual interpolation reach its target.
                                var renderedRobot = robotSurface.RenderTransform.TransformBounds(new Rect(0, 0, robotSurface.Width, robotSurface.Height));
                                Assert.Equal(180 * scale, renderedRobot.Width, 3);
                                Assert.Equal(200 * scale, renderedRobot.Height, 3);
                                Assert.Equal(Visibility.Visible, robotSurface.Visibility);
                                mini.RevealControls();
                                mini.UpdateLayout();
                                Assert.All(actionStrip.Children.OfType<Button>(), button =>
                                {
                                    Assert.Equal(42d, button.Width);
                                    Assert.Equal(40d, button.Height);
                                    Assert.Equal(miniVoiceButton.ActualWidth, button.ActualWidth);
                                    Assert.Equal(miniVoiceButton.ActualHeight, button.ActualHeight);
                                    // Layout rounding may move an edge by half a physical pixel.
                                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(button);
                                    Assert.InRange(Math.Abs(button.ActualWidth - button.Width), 0, 0.5 / dpi.DpiScaleX + 0.000001);
                                    Assert.InRange(Math.Abs(button.ActualHeight - button.Height), 0, 0.5 / dpi.DpiScaleY + 0.000001);
                                });
                                Assert.Same(runtime, mini.DataContext);
                                Assert.Same(liveSettings, shell.Page);
                                Assert.Same(draftPreferences, liveSettings.DataContext);
                                Assert.Same(liveApplicationTab, liveTabs.SelectedItem);
                                Assert.True(liveSettings.HasPendingChanges);
                                Assert.Equal("sapphire", liveStyleBox.SelectedValue);
                                Assert.Equal(2.5, livePitchSlider.Value);
                                AssertListeningUnchanged();
                                await Task.Delay(350);
                                Assert.Equal(scale, new SettingsStore(miniSettingsFile).Load().Settings.MiniRobotScale!.Value, 6);
                            }
                            // Continuous back-and-forth slider movement must not accumulate rounding drift.
                            foreach (var percent in Enumerable.Range(60, 101).Reverse().Concat(Enumerable.Range(60, 101)))
                            {
                                scaleSlider.Value = percent;
                                AssertResizeAnchor();
                            }
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            AssertResizeAnchor();
                            viewBox.SelectedValue = "panel";
                            Assert.False(scaleSlider.IsEnabled);
                            Assert.Equal("panel", runtime.Settings.MiniView);
                            Assert.Equal("robot", new SettingsStore(miniSettingsFile).Load().Settings.MiniView);
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            Assert.True(dialog.IsVisible);
                            Assert.Equal(1.6, runtime.Settings.MiniRobotScale);
                            Assert.Equal(Visibility.Collapsed, robotSurface.Visibility);
                            mini.CollapseControls();
                            Assert.True(mini.ControlsExpanded);
                            Assert.True(actionStrip.IsVisible);
                            Assert.True(actionStrip.IsHitTestVisible);
                            await Task.Delay(350);
                            Assert.Equal("panel", new SettingsStore(miniSettingsFile).Load().Settings.MiniView);
                            viewBox.SelectedValue = "robot";
                            Assert.True(scaleSlider.IsEnabled);
                            scaleSlider.Value = 117;
                            viewBox.SelectedValue = "panel";
                            Assert.Equal(1.17, runtime.Settings.MiniRobotScale);
                            Assert.Equal(1.6, new SettingsStore(miniSettingsFile).Load().Settings.MiniRobotScale);
                            // Closing before the debounce fires must retain and flush the latest values.
                            dialog.Close();
                            Assert.False(dialog.IsVisible);
                            Assert.Equal("panel", runtime.Settings.MiniView);
                            Assert.Equal(1.17, runtime.Settings.MiniRobotScale);
                            var persistedMini = new SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings;
                            Assert.Equal("panel", persistedMini.MiniView);
                            Assert.Equal(1.17, persistedMini.MiniRobotScale);
                            AssertListeningUnchanged();
                            openedMiniPreferences = true;
                        }
                        catch (Exception error) { miniPreferencesFailure = error; }
                        finally { dialog?.Close(); }
                    });
                    miniSettingsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    if (miniPreferencesFailure != null) ExceptionDispatchInfo.Capture(miniPreferencesFailure).Throw();
                    Assert.True(openedMiniPreferences);
                    miniSurface.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
                    await Task.Delay(450);
                    Assert.True(mini.ControlsExpanded);
                    Assert.True(actionStrip.IsVisible);
                    AssertListeningUnchanged();
                    window.ShowFromTray();
                    var navigation = Assert.IsType<ListBox>(shell.FindName("Navigation"));
                    var selectedSection = navigation.SelectedItem;
                    var sidebarToggle = Assert.IsType<Button>(shell.FindName("SidebarToggle"));
                    var sidebarColumn = Assert.IsType<ColumnDefinition>(shell.FindName("SidebarColumn"));
                    System.Windows.Input.FocusManager.SetFocusedElement(window, sidebarToggle);
                    foreach (var collapsed in new[] { true, false })
                    {
                        sidebarToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert.Equal(collapsed, shell.IsSidebarCollapsed);
                        Assert.Equal(collapsed ? 72d : 220d, sidebarColumn.Width.Value);
                        Assert.Same(liveSettings, shell.Page);
                        Assert.Same(draftPreferences, liveSettings.DataContext);
                        Assert.Same(liveApplicationTab, liveTabs.SelectedItem);
                        Assert.Same(selectedSection, navigation.SelectedItem);
                        Assert.Same(sidebarToggle, System.Windows.Input.FocusManager.GetFocusedElement(window));
                        Assert.True(liveSettings.HasPendingChanges);
                        Assert.Equal("sapphire", liveStyleBox.SelectedValue);
                        AssertListeningUnchanged();
                    }
                    var liveSave = (Button)liveSettings.FindName("SavePreferencesButton");
                    Assert.True(liveSave.IsEnabled);
                    liveSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    AssertListeningUnchanged();
                    Assert.Same(liveSettings, shell.Page);
                    Assert.Same(liveApplicationTab, liveTabs.SelectedItem);
                    Assert.Equal("light", runtime.Settings.Appearance);
                    Assert.Equal("sapphire", runtime.Settings.ColorStyle);
                    Assert.Equal(2.5, runtime.Settings.VoicePitch);
                    Assert.Equal("sapphire", ThemeManager.ColorStyle);
                    Assert.Equal("sapphire", new SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.ColorStyle);
                    Assert.Equal("panel", runtime.Settings.MiniView);
                    Assert.Equal(1.17, runtime.Settings.MiniRobotScale);
                    var savedAfterGeneralPreferences = new SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings;
                    Assert.Equal("panel", savedAfterGeneralPreferences.MiniView);
                    Assert.Equal(1.17, savedAfterGeneralPreferences.MiniRobotScale);
                    Assert.Equal(2.5, savedAfterGeneralPreferences.VoicePitch);
                    runtime.ApplySettings(runtime.Settings with { MiniView = "robot", MiniRobotScale = 1 });
                    Assert.Equal(Visibility.Visible, robotSurface.Visibility);
                    AssertListeningUnchanged();
                    // Applying another style while listening must retain both the live page and session.
                    foreach (var colorStyle in new[] { "emerald", "ruby", "amethyst", "graphite" })
                    {
                        liveStyleBox.SelectedValue = colorStyle;
                        Assert.True(liveSettings.HasPendingChanges);
                        liveSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        AssertListeningUnchanged();
                        Assert.Same(liveSettings, shell.Page);
                        Assert.Same(liveApplicationTab, liveTabs.SelectedItem);
                        Assert.False(liveSettings.HasPendingChanges);
                        Assert.Equal(colorStyle, liveStyleBox.SelectedValue);
                        Assert.Equal(colorStyle, runtime.Settings.ColorStyle);
                        Assert.Equal(colorStyle, ThemeManager.ColorStyle);
                        Assert.Equal(colorStyle, new SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.ColorStyle);
                        Assert.Equal("light", runtime.Settings.Appearance);
                    }
                    liveStyleBox.SelectedValue = "ruby";
                    Assert.True(liveSettings.HasPendingChanges);
                    Descendants(liveSettings).OfType<Button>().Single(b => Equals(b.Content, "Cancel"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    AssertListeningUnchanged();
                    Assert.Equal("graphite", runtime.Settings.ColorStyle);
                    Assert.Equal("graphite", ThemeManager.ColorStyle);
                    Assert.Equal("graphite", new SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.ColorStyle);
                    window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Escape)
                        { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                    AssertListeningUnchanged();
                    var voiceControl = Assert.IsType<Button>(window.FindName("VoiceControlButton"));
                    voiceControl.ApplyTemplate();
                    var micIcon = (FrameworkElement)voiceControl.Template.FindName("MicrophoneIcon", voiceControl);
                    var stopIcon = (FrameworkElement)voiceControl.Template.FindName("StopIcon", voiceControl);
                    foreach (var phase in new[] { AssistantPhase.WaitingForName, AssistantPhase.Listening,
                        AssistantPhase.Recognizing, AssistantPhase.Executing, AssistantPhase.Synthesizing, AssistantPhase.Speaking })
                    {
                        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Phase))!.SetValue(runtime, phase);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert.True(voiceControl.IsEnabled);
                        Assert.Equal(Visibility.Visible, stopIcon.Visibility);
                        Assert.Equal(Visibility.Collapsed, micIcon.Visibility);
                        Assert.Equal("Stop", System.Windows.Automation.AutomationProperties.GetName(voiceControl));
                    }
                    // The short gap before wake listening resumes must still offer Stop.
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(runtime, false);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.MicrophoneActive))!.SetValue(runtime, false);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(runtime.CanStop);
                    Assert.True(voiceControl.IsEnabled);
                    Assert.Equal(Visibility.Visible, stopIcon.Visibility);
                    voiceControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(listeningToken.IsCancellationRequested);
                    Assert.False(runtime.Busy);
                    Assert.False(runtime.MicrophoneActive);
                    Assert.False(pendingPower.HasPending);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.Ready))!.SetValue(runtime, true);
                    runtime.SelectedMicrophone = new Jarvis.App.Services.Microphone("fixture-no-capture", "Fixture microphone");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(voiceControl.IsEnabled);
                    Assert.False(runtime.CanStop);
                    Assert.Equal(Visibility.Visible, micIcon.Visibility);
                    Assert.Equal(Visibility.Collapsed, stopIcon.Visibility);
                    Assert.Equal("Start listening", voiceControl.ToolTip);
                    Assert.True(textCommandButton.IsEnabled);
                    var openedTextCommand = false;
                    Exception? textCommandFailure = null;
                    _ = window.Dispatcher.BeginInvoke(() =>
                    {
                        TextCommandWindow? dialog = null;
                        try
                        {
                            dialog = Assert.Single(window.OwnedWindows.OfType<TextCommandWindow>());
                            Assert.Same(runtime, dialog.DataContext);
                            openedTextCommand = true;
                        }
                        catch (Exception error) { textCommandFailure = error; }
                        finally { dialog?.Close(); }
                    });
                    textCommandButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (textCommandFailure != null) ExceptionDispatchInfo.Capture(textCommandFailure).Throw();
                    Assert.True(openedTextCommand);
                    window.ShowMini();
                    mini.Left = -10000; mini.Top = -10000;
                    mini.RevealControls();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(miniTextButton.IsEnabled);
                    // Sleep is decorative and only eligible while the shared runtime is idle.
                    var robot = Assert.IsType<Jarvis.App.Controls.MiniRobot>(mini.FindName("Robot"));
                    var sleepTimer = Assert.IsType<DispatcherTimer>(typeof(MiniWindow).GetField("_sleepTimer", privateFields)!.GetValue(mini));
                    Assert.Equal(TimeSpan.FromSeconds(45), sleepTimer.Interval);
                    sleepTimer.Interval = TimeSpan.FromMilliseconds(30);
                    mini.ResumeAfterDialog();
                    await Task.Delay(100);
                    Assert.True(robot.IsSleeping);
                    Assert.False(runtime.CanStop);
                    Assert.False(runtime.MicrophoneActive);
                    miniSurface.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
                    Assert.False(robot.IsSleeping);
                    Assert.True(mini.ControlsExpanded);
                    foreach (var phase in new[] { AssistantPhase.WaitingForName, AssistantPhase.Listening, AssistantPhase.Executing, AssistantPhase.Speaking, AssistantPhase.Fault })
                    {
                        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Phase))!.SetValue(runtime, phase);
                        await Task.Delay(60);
                        Assert.False(robot.IsSleeping);
                        Assert.False(sleepTimer.IsEnabled);
                    }
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.Phase))!.SetValue(runtime, AssistantPhase.Idle);
                    await Task.Delay(100);
                    Assert.True(robot.IsSleeping);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.MicrophoneActive))!.SetValue(runtime, true);
                    Assert.False(robot.IsSleeping);
                    Assert.False(sleepTimer.IsEnabled);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.MicrophoneActive))!.SetValue(runtime, false);
                    runtime.PreviewMiniSettings("panel", runtime.Settings.MiniRobotScale!.Value);
                    await Task.Delay(80);
                    Assert.False(robot.IsSleeping);
                    Assert.False(sleepTimer.IsEnabled);
                    runtime.PreviewMiniSettings("robot", runtime.Settings.MiniRobotScale!.Value);
                    window.HideMini();
                    await Task.Delay(80);
                    Assert.False(robot.IsSleeping);
                    Assert.False(sleepTimer.IsEnabled);
                    sleepTimer.Interval = TimeSpan.FromSeconds(45);
                    window.ShowMini();
                    mini.Left = -10000; mini.Top = -10000;
                    mini.RevealControls();
                    var openedMiniTextCommand = false;
                    Exception? miniTextCommandFailure = null;
                    _ = window.Dispatcher.BeginInvoke(async () =>
                    {
                        TextCommandWindow? dialog = null;
                        try
                        {
                            dialog = Assert.Single(mini.OwnedWindows.OfType<TextCommandWindow>());
                            Assert.Same(runtime, dialog.DataContext);
                            Assert.False(window.IsVisible);
                            // Restoring from the tray while this modal dialog is open keeps its owner visible.
                            window.ShowFromTray();
                            Assert.True(mini.IsVisible);
                            Assert.True(dialog.IsVisible);
                            Assert.False(window.IsVisible);
                            window.HideMini();
                            Assert.True(mini.IsVisible);
                            Assert.True(dialog.IsVisible);
                            // A modal dialog keeps the controls available; closing it must
                            // collapse immediately even though no new mouse event arrives.
                            miniSurface.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                                { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            Assert.True(mini.ControlsExpanded);
                            openedMiniTextCommand = true;
                        }
                        catch (Exception error) { miniTextCommandFailure = error; }
                        finally { dialog?.Close(); }
                    });
                    miniTextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (miniTextCommandFailure != null) ExceptionDispatchInfo.Capture(miniTextCommandFailure).Throw();
                    Assert.True(openedMiniTextCommand);
                    Assert.False(mini.ControlsExpanded);
                    Assert.False(actionStrip.IsVisible);
                    Assert.False(actionStrip.IsHitTestVisible);
                    window.ShowFromTray();
                    runtime.SelectedMicrophone = null;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.False(voiceControl.IsEnabled);
                    var textDialog = new TextCommandWindow { DataContext = runtime };
                    var textSubmit = Descendants(textDialog).OfType<Button>().Single();
                    ((TextBox)textDialog.FindName("CommandBox")).Text = "текст сохраняется";
                    Assert.True(textSubmit.IsEnabled);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(runtime, true);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.False(textSubmit.IsEnabled);
                    Assert.False(textCommandButton.IsEnabled);
                    // A stale click must not close the dialog or discard its input.
                    textSubmit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("текст сохраняется", textDialog.Command);
                    Assert.Null(textDialog.DialogResult);
                    textDialog.Close();
                    runtime.Stop();
                    // Mini's Stop button must reach the shared runtime, even without a capture device.
                    var miniOperation = (CancellationTokenSource)typeof(MainViewModel).GetField("_operation", privateFields)!.GetValue(runtime)!;
                    var miniListeningToken = miniOperation.Token;
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(runtime, true);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.MicrophoneActive))!.SetValue(runtime, true);
                    window.ShowMini();
                    mini.RevealControls();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(miniVoiceButton.IsEnabled);
                    Assert.Equal("Stop", System.Windows.Automation.AutomationProperties.GetName(miniVoiceButton));
                    miniVoiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(miniListeningToken.IsCancellationRequested);
                    Assert.False(runtime.Busy);
                    Assert.False(runtime.MicrophoneActive);
                    window.ShowFromTray();
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.Ready))!.SetValue(runtime, false);
                    runtime.ApplySettings(runtime.Settings with { Appearance = "dark" });
                    Assert.True(shell.Navigate!("settings"));
                    var settingsPage = Assert.IsType<SettingsPage>(shell.Page);
                    var operationFeedback = Assert.IsType<TextBlock>(settingsPage.FindName("OperationFeedback"));
                    var saveFeedback = Assert.IsType<TextBlock>(settingsPage.FindName("SaveFeedback"));
                    Assert.Equal(Visibility.Collapsed, operationFeedback.Visibility);
                    Assert.True(string.IsNullOrEmpty(saveFeedback.Text));
                    Descendants(settingsPage).OfType<Button>().Single(button => Equals(button.Content, settingsPage.FindResource("Ui8BC912B90E03")))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(Visibility.Visible, operationFeedback.Visibility);
                    Assert.False(runtime.MicrophoneActive);
                    var settingsTabs = Assert.IsType<TabControl>(settingsPage.FindName("SettingsTabs"));
                    var applicationTab = Assert.IsType<TabItem>(settingsPage.FindName("ApplicationTab"));
                    settingsTabs.SelectedItem = applicationTab;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.False(settingsPage.HasPendingChanges);
                    var styleBox = Assert.IsType<ComboBox>(settingsPage.FindName("ColorStyleBox"));
                    Assert.True(styleBox.IsEnabled);
                    Assert.Equal("graphite", styleBox.SelectedValue);
                    Assert.Equal(new[] { "graphite", "sapphire", "emerald", "ruby", "amethyst" },
                        styleBox.Items.Cast<Choice>().Select(choice => choice.Id));
                    var appearanceBox = Assert.IsType<ComboBox>(settingsPage.FindName("AppearanceBox"));
                    var darkBackground = window.Background;
                    appearanceBox.SelectedValue = "light";
                    Assert.True(settingsPage.HasPendingChanges);
                    ((Button)settingsPage.FindName("SavePreferencesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Same(settingsPage, shell.Page);
                    Assert.Same(applicationTab, settingsTabs.SelectedItem);
                    Assert.False(settingsPage.HasPendingChanges);
                    Assert.Equal("light", runtime.Settings.Appearance);
                    Assert.False(string.IsNullOrWhiteSpace(saveFeedback.Text));
                    Assert.Equal(Visibility.Collapsed, operationFeedback.Visibility);
                    Assert.Equal("light", new Jarvis.Core.SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.Appearance);
                    Assert.Equal("light", ThemeManager.EffectiveTheme);
                    Assert.NotEqual(darkBackground, window.Background);
                    Assert.Equal(application.Resources["ThemeWindow"], window.Background);
                    foreach (var appearance in new[] { "dark", "light" })
                    {
                        appearanceBox.SelectedValue = appearance;
                        Assert.True(settingsPage.HasPendingChanges);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert.True(string.IsNullOrEmpty(saveFeedback.Text));
                        ((Button)settingsPage.FindName("SavePreferencesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert.Same(settingsPage, shell.Page);
                        Assert.Same(applicationTab, settingsTabs.SelectedItem);
                        Assert.False(settingsPage.HasPendingChanges);
                        Assert.False(string.IsNullOrWhiteSpace(saveFeedback.Text));
                        Assert.Equal(appearance, runtime.Settings.Appearance);
                        Assert.Equal(appearance, new Jarvis.Core.SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.Appearance);
                        Assert.Equal(appearance, ThemeManager.EffectiveTheme);
                        Assert.Equal(appearance, appearanceBox.SelectedValue);
                        Assert.Equal(application.Resources["ThemeWindow"], window.Background);
                    }
                    var preferences = Assert.IsType<SettingsViewModel>(settingsPage.DataContext);
                    var languageBox = Descendants(settingsPage).OfType<ComboBox>().Single(box => ReferenceEquals(box.ItemsSource, preferences.Languages));
                    languageBox.SelectedValue = "ru";
                    Assert.True(settingsPage.HasPendingChanges);
                    ((Button)settingsPage.FindName("SavePreferencesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Same(settingsPage, shell.Page);
                    Assert.Same(applicationTab, settingsTabs.SelectedItem);
                    Assert.False(settingsPage.HasPendingChanges);
                    Assert.Equal("ru", languageBox.SelectedValue);
                    Assert.Equal("ru", new Jarvis.Core.SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.InterfaceLanguage);
                    Assert.Equal(new[] { "Системное", "Светлое", "Тёмное" }, appearanceBox.Items.Cast<Choice>().Select(choice => choice.Label));
                    appearanceBox.SelectedValue = "dark";
                    styleBox.SelectedValue = "amethyst";
                    Assert.True(settingsPage.HasPendingChanges);
                    Descendants(settingsPage).OfType<Button>().Single(b => Equals(b.Content, "Отмена")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("light", runtime.Settings.Appearance);
                    Assert.Equal("light", new Jarvis.Core.SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.Appearance);
                    Assert.Equal("light", ThemeManager.EffectiveTheme);
                    Assert.Equal("graphite", runtime.Settings.ColorStyle);
                    Assert.Equal("graphite", new SettingsStore(Path.Combine(root, "data", "settings.json")).Load().Settings.ColorStyle);
                    Assert.Equal("graphite", ThemeManager.ColorStyle);
                    runtime.ApplySettings(runtime.Settings with { Appearance = "system" });
                    Assert.Equal(ThemeManager.Resolve("system", ThemeManager.ReadSystemIsDark()), ThemeManager.EffectiveTheme);
                    runtime.ApplySettings(runtime.Settings with { Appearance = "dark" });
                    runtime.ApplySettings(runtime.Settings with { InterfaceLanguage = "ru" });
                    Assert.Equal("JARVIS", window.Title);
                    Assert.Equal("JARVIS не слушает", runtime.MicrophoneLabel);

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
                    Assert.DoesNotContain(mini, application.Windows.Cast<Window>());

                    window = new MainWindow(root, ["--autostart"]) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                    closed = false;
                    window.Closed += (_, _) => closed = true;
                    window.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.False(window.IsVisible);
                    Assert.False(((MainViewModel)window.DataContext).MicrophoneActive);
                    window.ShowFromTray();
                    ChooseClose(dialog => Descendants(dialog).OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(closed);

                    window = new MainWindow(root, []) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                    closed = false;
                    window.Closed += (_, _) => closed = true;
                    window.Show();
                    var selfRuntime = (MainViewModel)window.DataContext;
                    // Text exit needs no speech worker, microphone, account or external app.
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.Ready))!.SetValue(selfRuntime, true);
                    await selfRuntime.RunTextAsync("закрой себя");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(closed);
                    Assert.False(selfRuntime.MicrophoneActive);

                    // Inject a recorder: exercising confirmation must never power down the test machine.
                    var fakePower = new RecordingPowerControl();
                    await using (var powerRuntime = new MainViewModel(root, [], power: fakePower))
                    {
                        void ReadyForText() => typeof(MainViewModel).GetProperty(nameof(MainViewModel.Ready))!.SetValue(powerRuntime, true);
                        ReadyForText();
                        foreach (var phrase in new[] { "выключи компухтер", "перезагрузи компудахтер", "переведи компутер в режим сна", "гибернация" })
                        {
                            var before = fakePower.Requests.Count;
                            await powerRuntime.RunTextAsync(phrase);
                            Assert.True(powerRuntime.AwaitingChoice);
                            Assert.Equal(before, fakePower.Requests.Count);
                            powerRuntime.Stop(keepChoice: true);
                            await powerRuntime.RunTextAsync("да подтверждаю");
                            Assert.Equal(before + 1, fakePower.Requests.Count);
                            Assert.False(powerRuntime.AwaitingChoice);
                            Assert.False(powerRuntime.MicrophoneActive);
                        }
                        Assert.Equal(new[] { Jarvis.Core.Intent.ShutdownComputer, Jarvis.Core.Intent.RestartComputer, Jarvis.Core.Intent.SleepComputer, Jarvis.Core.Intent.HibernateComputer }, fakePower.Requests);
                        await powerRuntime.RunTextAsync("выключи компьютер");
                        await powerRuntime.RunTextAsync("спроси нейросеть подтверди выключение");
                        Assert.Equal("Команда питания отменена.", powerRuntime.Reply);
                        Assert.False(powerRuntime.AwaitingChoice);
                        Assert.Equal(4, fakePower.Requests.Count);
                        await powerRuntime.RunTextAsync("выключи компьютер");
                        powerRuntime.Stop();
                        Assert.False(powerRuntime.AwaitingChoice);
                        await powerRuntime.RunTextAsync("да подтверждаю");
                        Assert.Equal(4, fakePower.Requests.Count);
                    }

                    window = new MainWindow(root, []) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                    closed = false;
                    window.Closed += (_, _) => closed = true;
                    window.Show();
                    window.ShowMini();
                    var endingMini = Assert.IsType<MiniWindow>(window.Mini);
                    endingMini.Left = -10000; endingMini.Top = -10000;
                    Assert.True(endingMini.IsVisible);
                    window.EndWindowsSession();
                    window.Close();
                    Assert.True(closed);
                    Assert.DoesNotContain(endingMini, application.Windows.Cast<Window>());
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    window?.EndWindowsSession();
                    application.Shutdown();
                    File.Delete(Path.Combine(root, "data", "settings.json"));
                    File.Delete(Path.Combine(root, "data", "ai-connections.json"));
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

    private static async Task VerifyLongReplyRecovery(MainViewModel runtime, MainWindow window)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Set(string property, object? value) => typeof(MainViewModel).GetProperty(property)!.SetValue(runtime, value);
        var workerField = typeof(MainViewModel).GetField("_worker", flags)!;
        var previousWorker = workerField.GetValue(runtime);
        if (previousWorker is not null)
            Assert.Null(typeof(Jarvis.App.Services.WorkerClient).GetField("_process", flags)!.GetValue(previousWorker));
        var wasReady = runtime.Ready; var phase = runtime.Phase; var reply = runtime.Reply; var mic = runtime.SelectedMicrophone;
        await using var worker = new Jarvis.App.Services.WorkerClient(); // Queue only: never starts Python or audio.
        workerField.SetValue(runtime, worker);
        var channel = (System.Threading.Channels.Channel<string>)typeof(Jarvis.App.Services.WorkerClient).GetField("_outgoing", flags)!.GetValue(worker)!;
        var speak = typeof(MainViewModel).GetMethod("Speak", flags)!;
        var next = typeof(MainViewModel).GetMethod("SendNextSpeechPart", flags)!;
        long Session() => (long)typeof(MainViewModel).GetField("_session", flags)!.GetValue(runtime)!;
        Task Receive(object message) => (Task)typeof(MainViewModel).GetMethod("HandleAsync", flags)!.Invoke(runtime, [System.Text.Json.JsonSerializer.SerializeToElement(message)])!;
        try
        {
            Set(nameof(MainViewModel.Ready), true);
            runtime.SelectedMicrophone = new Jarvis.App.Services.Microphone("fixture", "No capture");
            var text = string.Concat(Enumerable.Repeat("Это длинный ответ нейросети. Весь текст должен остаться доступен.\n", 35));
            Set(nameof(MainViewModel.Reply), text); Set(nameof(MainViewModel.Busy), true);
            var session = Session();
            speak.Invoke(runtime, [session, text, runtime.Settings]);
            var chunks = new List<string>();
            do
            {
                Assert.True(channel.Reader.TryRead(out var line));
                using var message = System.Text.Json.JsonDocument.Parse(line!);
                Assert.Equal("synthesize", message.RootElement.GetProperty("type").GetString());
                Assert.Equal(session, message.RootElement.GetProperty("session").GetInt64());
                chunks.Add(message.RootElement.GetProperty("text").GetString()!);
            } while ((bool)next.Invoke(runtime, [session])!);
            Assert.True(chunks.Count > 1); Assert.Equal(text, string.Concat(chunks));
            Assert.All(chunks, part => Assert.InRange(part.Length, 1, 400));
            Assert.Equal(text, runtime.Reply);

            speak.Invoke(runtime, [session, text, runtime.Settings]);
            await Receive(new { type = "error", session, text = "Reply is too long" });
            Assert.True(runtime.Ready); Assert.True(runtime.CanSpeak); Assert.True(runtime.CanListen);
            Assert.False(runtime.Busy); Assert.False(runtime.CanRestart); Assert.Equal(text, runtime.Reply);
            Assert.Empty((Queue<string>)typeof(MainViewModel).GetField("_speechParts", flags)!.GetValue(runtime)!);
            Assert.False((bool)next.Invoke(runtime, [session])!); // Canceled chunks cannot restart.
            await Receive(new { type = "error", session, text = "Stale failure" });
            Assert.True(runtime.Ready);
            await Receive(new { type = "fatal", text = "Worker stopped" });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(runtime.Ready); Assert.True(runtime.CanRestart);
            var restart = Assert.IsType<Button>(window.FindName("RestartWorkerButton"));
            Assert.True(restart.IsVisible); Assert.True(restart.IsEnabled);
            Assert.False(((Button)window.FindName("VoiceControlButton")).IsVisible);
        }
        finally
        {
            runtime.Stop(); workerField.SetValue(runtime, previousWorker);
            Set(nameof(MainViewModel.Ready), wasReady); Set(nameof(MainViewModel.Phase), phase);
            Set(nameof(MainViewModel.Reply), reply); runtime.SelectedMicrophone = mic;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class RecordingPowerControl : Jarvis.App.Services.IPowerControl
    {
        public List<Jarvis.Core.Intent> Requests { get; } = [];
        public Task RequestAsync(Jarvis.Core.Intent intent, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests.Add(intent); return Task.CompletedTask; }
    }
}
