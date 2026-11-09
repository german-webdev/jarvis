using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Jarvis.Core;

namespace Jarvis.App.Controls;

/// <summary>Resolution-independent companion artwork; it never starts audio or assistant work.</summary>
public partial class MiniRobot : UserControl
{
    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(AssistantPhase), typeof(MiniRobot), new PropertyMetadata(AssistantPhase.Idle, StateChanged));
    public static readonly DependencyProperty MicrophoneActiveProperty = DependencyProperty.Register(
        nameof(MicrophoneActive), typeof(bool), typeof(MiniRobot), new PropertyMetadata(false, StateChanged));
    public static readonly DependencyProperty CanStopProperty = DependencyProperty.Register(
        nameof(CanStop), typeof(bool), typeof(MiniRobot), new PropertyMetadata(false, StateChanged));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(MiniRobot), new PropertyMetadata(false, StateChanged));
    public static readonly DependencyProperty IsSleepingProperty = DependencyProperty.Register(
        nameof(IsSleeping), typeof(bool), typeof(MiniRobot), new PropertyMetadata(false, StateChanged));

    public AssistantPhase Phase { get => (AssistantPhase)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public bool MicrophoneActive { get => (bool)GetValue(MicrophoneActiveProperty); set => SetValue(MicrophoneActiveProperty, value); }
    public bool CanStop { get => (bool)GetValue(CanStopProperty); set => SetValue(CanStopProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    public bool IsSleeping { get => (bool)GetValue(IsSleepingProperty); set => SetValue(IsSleepingProperty, value); }
    private bool ShowSleep => IsSleeping && Phase == AssistantPhase.Idle && !CanStop && !MicrophoneActive;

    private readonly DispatcherTimer _blinkTimer;
    private bool _subscribed;
    private bool _processing;

    public MiniRobot()
    {
        InitializeComponent();
        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(4.8) };
        _blinkTimer.Tick += Blink;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => RefreshMotion();
        ApplyState();
    }

    private static void StateChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var robot = (MiniRobot)owner;
        // A binding can resolve while InitializeComponent is still constructing the artwork.
        if (robot._blinkTimer is not null) robot.ApplyState();
    }

    private void ApplyState()
    {
        _processing = Phase is AssistantPhase.Recognizing or AssistantPhase.Executing or AssistantPhase.Synthesizing;
        var preparing = Phase == AssistantPhase.Loading || (Phase == AssistantPhase.Idle && CanStop && !MicrophoneActive);
        string left = "M 51,69 L 70,69", right = "M 110,69 L 129,69";
        string? mouth = "M 83,90 Q 90,95 97,90";
        Eyes.Opacity = 1;
        ChestRing.Opacity = 0.95;
        ChestMark.Visibility = Visibility.Collapsed;

        if (ShowSleep)
        {
            left = "M 52,68 Q 61,76 70,68";
            right = "M 110,68 Q 119,76 128,68";
            Eyes.Opacity = 0.55;
            ChestRing.Opacity = 0.3;
        }
        else if (Phase == AssistantPhase.Fault)
        {
            // Report the fault through the chest mark, without an angry or sad face.
            left = "M 53,69 L 68,69";
            right = "M 112,69 L 127,69";
            ChestMark.Visibility = Visibility.Visible;
            ChestRing.Opacity = 0.35;
        }
        else if (Phase == AssistantPhase.Speaking)
        {
            left = "M 52,71 Q 60,59 69,71";
            right = "M 111,71 Q 120,59 128,71";
            mouth = "M 83,89 L 83,93 M 90,86 L 90,96 M 97,89 L 97,93";
        }
        else if (_processing)
        {
            // A gently raised, curved eye reads as curiosity, not inward-slanting brows.
            left = "M 52,69 Q 60,63 69,69";
            right = "M 111,69 L 128,69";
            ChestRing.Opacity = 0.2;
        }
        else if (preparing)
        {
            left = "M 53,71 Q 61,75 69,71";
            right = "M 111,71 Q 119,75 127,71";
            Eyes.Opacity = 0.65;
            ChestRing.Opacity = 0.45;
        }
        else if (Phase == AssistantPhase.Listening || (MicrophoneActive && Phase != AssistantPhase.WaitingForName))
        {
            left = "M 60,61 L 60,76";
            right = "M 120,61 L 120,76";
        }
        else if (Phase == AssistantPhase.WaitingForName)
        {
            left = "M 60,64 L 60,73";
            right = "M 120,64 L 120,73";
        }

        LeftEye.Data = Geometry.Parse(left);
        RightEye.Data = Geometry.Parse(right);
        Mouth.Data = mouth is null ? null : Geometry.Parse(mouth);
        Mouth.Visibility = mouth is null ? Visibility.Collapsed : Visibility.Visible;
        ChestArc.Visibility = _processing ? Visibility.Visible : Visibility.Collapsed;
        RefreshMotion();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed)
        {
            SystemParameters.StaticPropertyChanged += SystemPreferencesChanged;
            _subscribed = true;
        }
        RefreshMotion();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_subscribed)
        {
            SystemParameters.StaticPropertyChanged -= SystemPreferencesChanged;
            _subscribed = false;
        }
        StopMotion();
    }

    private void SystemPreferencesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or null or "")
        {
            if (Dispatcher.CheckAccess()) RefreshMotion();
            else Dispatcher.BeginInvoke(new Action(RefreshMotion));
        }
    }

    private bool MotionAllowed => IsLoaded && IsVisible && !ReduceMotion && SystemParameters.ClientAreaAnimation;

    private void RefreshMotion()
    {
        if (_blinkTimer is null) return;
        StopMotion();
        if (!MotionAllowed) return;
        if (ShowSleep)
        {
            ChestRing.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 0.65, TimeSpan.FromSeconds(1.8))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
            return;
        }
        if (Phase is not AssistantPhase.Fault and not AssistantPhase.Loading) _blinkTimer.Start();
        if (_processing)
        {
            ChestRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }
    }

    private void StopMotion()
    {
        _blinkTimer.Stop();
        EyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ChestRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        ChestRing.BeginAnimation(OpacityProperty, null);
    }

    private void Blink(object? sender, EventArgs args)
    {
        if (!MotionAllowed) { StopMotion(); return; }
        EyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.12, TimeSpan.FromMilliseconds(95))
        {
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }
}
