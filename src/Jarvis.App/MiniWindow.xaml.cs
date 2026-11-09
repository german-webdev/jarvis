using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Jarvis.App;

/// <summary>A view of the existing runtime: hiding this window never stops the assistant.</summary>
public partial class MiniWindow : Window
{
    private readonly MainViewModel _runtime;
    private readonly Action _toggleVoice, _openText, _openMain, _hideMini;
    private readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private readonly ContextMenu _menu = new();
    private readonly MenuItem _settingsItem = new(), _hideItem = new();
    private bool _exit, _keyboardNavigation, _panelView, _presentationApplied;
    private double _robotScale;
    private NativeRect? _resizeAnchor;
    private HwndSource? _source;
    public bool ControlsExpanded { get; private set; }

    public MiniWindow(MainViewModel runtime, Action toggleVoice, Action openText, Action openMain, Action hideMini)
    {
        _runtime = runtime; _toggleVoice = toggleVoice; _openText = openText; _openMain = openMain; _hideMini = hideMini;
        InitializeComponent(); DataContext = runtime;
        _menu.SetResourceReference(BackgroundProperty, "ThemeSurface");
        _menu.SetResourceReference(ForegroundProperty, "ThemeText");
        _settingsItem.Click += (_, _) => OpenSettings(); _hideItem.Click += (_, _) => _hideMini();
        _menu.Items.Add(_settingsItem); _menu.Items.Add(_hideItem);
        _menu.Closed += (_, _) => ResumeAfterDialog();
        _sleepTimer.Tick += SleepTimerTick;
        Ui.Changed += RefreshLanguage; RefreshLanguage();
        _runtime.PropertyChanged += RuntimePropertyChanged;
        ApplyPresentation();
        PreviewKeyDown += OnKeyDown;
        Deactivated += (_, _) => { _keyboardNavigation = false; CollapseIfAllowed(); };
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { CollapseIfAllowed(); WakeRobot(); }));
        IsVisibleChanged += (_, _) => { if (!IsVisible) { _menu.IsOpen = false; CollapseControls(); } else ConstrainToScreen(); WakeRobot(); };
        Closing += (_, e) => { if (!_exit) { e.Cancel = true; _hideMini(); } };
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); _source?.AddHook(WindowMessage);
            var screen = Forms.Screen.PrimaryScreen!;
            var handle = new WindowInteropHelper(this).Handle;
            GetWindowRect(handle, out var bounds);
            SetWindowPos(handle, 0, screen.WorkingArea.Right - (bounds.Right - bounds.Left) - 24,
                screen.WorkingArea.Bottom - (bounds.Bottom - bounds.Top) - 24, 0, 0, 0x15);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ConstrainToScreen);
    }

    private bool HasDialog => OwnedWindows.Cast<Window>().Any(window => window.IsVisible);
    private bool CanSleep => !_exit && IsVisible && !_panelView && !IsMouseOver && !_menu.IsOpen && !HasDialog
        && !(_keyboardNavigation && IsKeyboardFocusWithin) && _runtime.Phase == Jarvis.Core.AssistantPhase.Idle
        && !_runtime.CanStop && !_runtime.MicrophoneActive;
    private void WakeRobot()
    {
        _sleepTimer.Stop(); Robot.IsSleeping = false;
        if (CanSleep) _sleepTimer.Start();
    }
    private void SleepTimerTick(object? sender, EventArgs e)
    {
        _sleepTimer.Stop();
        if (CanSleep) Robot.IsSleeping = true;
    }
    private UIElement FocusSurface => _panelView ? Platform : RobotSurface;
    private void RefreshLanguage()
    {
        Label(MiniTextButton, "Команда текстом"); Label(MiniOpenButton, "Открыть JARVIS");
        _settingsItem.Header = Ui.Text("Настройки Mini JARVIS"); _hideItem.Header = Ui.Text("Скрыть Mini JARVIS");
        static void Label(Button button, string text) { button.ToolTip = Ui.Text(text); AutomationProperties.SetName(button, Ui.Text(text)); }
    }

    private void RuntimePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(MainViewModel.Settings) or nameof(MainViewModel.Phase)
            or nameof(MainViewModel.CanStop) or nameof(MainViewModel.MicrophoneActive))) return;
        void Refresh()
        {
            if (_exit) return;
            if (e.PropertyName is null or "" or nameof(MainViewModel.Settings)) ApplyPresentation();
            WakeRobot();
        }
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    private void ApplyPresentation()
    {
        if (_exit) return;
        var panel = _runtime.Settings.MiniView == "panel";
        var scale = _runtime.Settings.MiniRobotScale ?? 1;
        if (_presentationApplied && panel == _panelView && scale == _robotScale) return;

        // Scaling is render-only around the soles (artwork y=194 of 200) inside a fixed envelope.
        // Resizing/moving the HWND for
        // each slider value exposes intermediate frames and accumulates pixel rounding.
        // Only switching between robot and panel changes the native window dimensions.
        var changeWindowSize = !_presentationApplied || panel != _panelView;
        var handle = new WindowInteropHelper(this).Handle;
        if (changeWindowSize && _resizeAnchor == null && handle != 0 && GetWindowRect(handle, out var before)) _resizeAnchor = before;
        var wasPanel = _panelView;
        _panelView = panel; _robotScale = scale; _presentationApplied = true;
        SetRobotScale(scale, animate: !changeWindowSize);
        HoverSurface.Width = panel ? 178 : Math.Max(178, 180 * scale);
        HoverSurface.Height = panel ? 56 : 200 * scale + 56;
        RobotSurface.Visibility = panel ? Visibility.Collapsed : Visibility.Visible;
        Platform.Focusable = panel;
        if (changeWindowSize)
        {
            Platform.Margin = new Thickness(0, panel ? 8 : 200 * 1.6 + 2, 0, 0);
            Width = panel ? 210 : 180 * 1.6 + 48;
            Height = panel ? 80 : 200 * 1.6 + 72;
        }
        if (panel || IsMouseOver || HasDialog || (ControlsExpanded && !wasPanel)) RevealControls();
        else CollapseControls();

        if (changeWindowSize && handle != 0) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_exit) return;
            UpdateLayout();
            if (_resizeAnchor is { } anchor && GetWindowRect(handle, out var after))
            {
                _resizeAnchor = null;
                var x = anchor.Left + ((anchor.Right - anchor.Left) - (after.Right - after.Left)) / 2;
                var y = anchor.Bottom - (after.Bottom - after.Top);
                SetWindowPos(handle, 0, x, y, 0, 0, 0x15);
            }
            ConstrainToScreen();
        }));
    }

    private void SetRobotScale(double scale, bool animate)
    {
        // Retarget from the currently drawn size, not the previous slider value.
        // One shared clock keeps both axes uniform even when the pointer reverses.
        var from = RobotScale.ScaleX;
        RobotScale.ScaleX = scale; RobotScale.ScaleY = scale;
        if (animate && IsVisible && !_runtime.ReduceMotion && SystemParameters.ClientAreaAnimation && Math.Abs(from - scale) > 0.000001)
        {
            var clock = new DoubleAnimation(from, scale, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            }.CreateClock();
            RobotScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, clock);
            RobotScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, clock);
        }
        else
        {
            RobotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RobotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    public void RevealControls()
    {
        WakeRobot();
        if (ControlsExpanded) return;
        ControlsExpanded = true;
        HoverSurface.Visibility = Visibility.Visible;
        Platform.CornerRadius = new CornerRadius(14);
        ActionStrip.Visibility = Visibility.Visible; ActionStrip.IsHitTestVisible = true; PlatformMark.Visibility = Visibility.Hidden;
        AnimatePlatform(178, 56);
    }
    public void CollapseControls()
    {
        if (_panelView) { RevealControls(); return; }
        _keyboardNavigation = false;
        if (!ControlsExpanded) return;
        ControlsExpanded = false;
        HoverSurface.Visibility = Visibility.Collapsed;
        Platform.CornerRadius = new CornerRadius(5);
        ActionStrip.IsHitTestVisible = false; ActionStrip.Visibility = Visibility.Hidden; PlatformMark.Visibility = Visibility.Visible;
        AnimatePlatform(104, 18);
    }
    private void AnimatePlatform(double width, double height)
    {
        var duration = TimeSpan.FromMilliseconds(_runtime.ReduceMotion || !SystemParameters.ClientAreaAnimation || !IsVisible ? 0 : 160);
        Platform.BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Platform.BeginAnimation(HeightProperty, new DoubleAnimation(height, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void CollapseIfAllowed()
    {
        if (!_exit && !IsMouseOver && !_menu.IsOpen && !HasDialog && !(_keyboardNavigation && IsKeyboardFocusWithin)) CollapseControls();
    }
    public void ResumeAfterDialog() { if (IsVisible) { CollapseIfAllowed(); WakeRobot(); } }
    private void SurfaceEnter(object sender, MouseEventArgs e) => RevealControls();
    private void SurfaceLeave(object sender, MouseEventArgs e) { CollapseIfAllowed(); WakeRobot(); }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        WakeRobot();
        if (e.Key == Key.Tab) { _keyboardNavigation = true; RevealControls(); }
        if (e.Key == Key.Enter && ReferenceEquals(Keyboard.FocusedElement, FocusSurface)) { _keyboardNavigation = true; RevealControls(); MiniTextButton.Focus(); e.Handled = true; }
        if (e.Key == Key.Escape && !_menu.IsOpen) { FocusSurface.Focus(); CollapseControls(); e.Handled = true; }
        if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) { OpenMenu(); e.Handled = true; }
    }
    private void DragSurface(object sender, MouseButtonEventArgs e)
    {
        WakeRobot();
        _keyboardNavigation = false;
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is Button) return;
        FocusSurface.Focus();
        if (e.LeftButton == MouseButtonState.Pressed) { DragMove(); ConstrainToScreen(); }
    }
    private void TextClick(object sender, RoutedEventArgs e) { if (_runtime.CanEnterText) _openText(); }
    private void VoiceClick(object sender, RoutedEventArgs e) { if (_runtime.CanToggleListening) _toggleVoice(); }
    private void OpenClick(object sender, RoutedEventArgs e) => _openMain();
    private void OpenSettings()
    {
        try { new MiniSettingsWindow(_runtime) { Owner = this }.ShowDialog(); }
        finally { ResumeAfterDialog(); }
    }
    private void ShowContextMenu(object sender, MouseButtonEventArgs e) { OpenMenu(); e.Handled = true; }
    private void OpenMenu() { RevealControls(); _menu.PlacementTarget = Platform; _menu.IsOpen = true; }

    public void ConstrainToScreen() => ConstrainWindow(this);
    internal static void ConstrainWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var rect)) return;
        var area = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)).WorkingArea;
        var x = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - (rect.Right - rect.Left)));
        var y = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top)));
        if (x != rect.Left || y != rect.Top) SetWindowPos(handle, 0, x, y, 0, 0, 0x15);
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x007E) Dispatcher.BeginInvoke(ConstrainToScreen); // Display topology changed.
        return 0;
    }
    public void DisposeForExit()
    {
        if (_exit) return;
        _exit = true; _sleepTimer.Stop(); _sleepTimer.Tick -= SleepTimerTick; _menu.IsOpen = false;
        Ui.Changed -= RefreshLanguage; _runtime.PropertyChanged -= RuntimePropertyChanged;
        _source?.RemoveHook(WindowMessage); Close();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
