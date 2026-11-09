using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Jarvis.App;

public partial class MiniSettingsWindow : Window
{
    private readonly MainViewModel _runtime;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _updatingControls = true;
    private bool _pendingSave;

    public MiniSettingsWindow(MainViewModel runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        RefreshLanguage();
        MiniViewBox.SelectedValue = runtime.Settings.MiniView;
        RobotScaleSlider.Value = runtime.Settings.Normalize().MiniRobotScale!.Value * 100;
        UpdateScaleControls();
        _updatingControls = false;
        _saveTimer.Tick += SaveTimerTick;
        Ui.Changed += RefreshLanguage;
        Closing += OnClosing;
        Closed += OnClosed;
        SourceInitialized += (_, _) =>
        {
            if (Owner is { } owner) Services.CompanionPlacement.Attach(this, owner);
        };
    }

    private void RefreshLanguage()
    {
        var wasUpdating = _updatingControls;
        _updatingControls = true;
        try
        {
            var view = MiniViewBox.SelectedValue as string ?? _runtime.Settings.MiniView;
            Title = Ui.Text("Настройки Mini JARVIS");
            ViewLabel.Content = Ui.Text("Вид Mini JARVIS");
            SizeLabel.Content = Ui.Text("Размер робота");
            AutomationProperties.SetName(MiniViewBox, Ui.Text("Вид Mini JARVIS"));
            AutomationProperties.SetName(RobotScaleSlider, Ui.Text("Размер робота"));
            MiniViewBox.ItemsSource = new[] { new Choice("robot", Ui.Text("С роботом")), new Choice("panel", Ui.Text("Только панель")) };
            MiniViewBox.SelectedValue = view;
            UpdateScaleControls();
        }
        finally { _updatingControls = wasUpdating; }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyLiveSettings();
    private void RobotScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyLiveSettings();

    private void UpdateScaleControls()
    {
        RobotScaleSlider.IsEnabled = Equals(MiniViewBox.SelectedValue, "robot");
        RobotScaleLabel.Text = $"{RobotScaleSlider.Value:0}%";
    }

    private void ApplyLiveSettings()
    {
        if (_updatingControls) return;
        UpdateScaleControls();
        _runtime.PreviewMiniSettings(MiniViewBox.SelectedValue as string ?? "robot", RobotScaleSlider.Value / 100);
        _pendingSave = true;
        ErrorFeedback.Visibility = Visibility.Collapsed;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimerTick(object? sender, EventArgs e) => FlushSettings();

    private bool FlushSettings()
    {
        _saveTimer.Stop();
        if (!_pendingSave) return true;
        try
        {
            _runtime.PersistMiniSettings();
            _pendingSave = false;
            ErrorFeedback.Text = "";
            ErrorFeedback.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception error)
        {
            ErrorFeedback.Text = Ui.Text("Не удалось сохранить настройки") + ": " + error.Message;
            ErrorFeedback.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!FlushSettings()) e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTimerTick;
        Ui.Changed -= RefreshLanguage;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        base.OnPreviewKeyDown(e);
    }
}
