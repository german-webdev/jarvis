using System.Windows;
using System.Windows.Media;
using Jarvis.Core;

namespace Jarvis.App.Controls;

public sealed class VoiceVisualizer : FrameworkElement
{
    public static readonly DependencyProperty AccentBrushProperty = ThemeBrush("AccentBrush");
    public static readonly DependencyProperty IdleBrushProperty = ThemeBrush("IdleBrush");
    public static readonly DependencyProperty ErrorBrushProperty = ThemeBrush("ErrorBrush");
    public static readonly DependencyProperty SpeakingBrushProperty = ThemeBrush("SpeakingBrush");
    private static DependencyProperty ThemeBrush(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(VoiceVisualizer),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(VoiceVisualizer), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(nameof(Phase), typeof(AssistantPhase), typeof(VoiceVisualizer), new FrameworkPropertyMetadata(AssistantPhase.Idle, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(nameof(ReduceMotion), typeof(bool), typeof(VoiceVisualizer), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public AssistantPhase Phase { get => (AssistantPhase)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    private double _time;
    private double _last;
    private double _smoothed;

    public VoiceVisualizer()
    {
        SetResourceReference(AccentBrushProperty, "ThemeAccentInk");
        SetResourceReference(IdleBrushProperty, "ThemeMuted");
        SetResourceReference(ErrorBrushProperty, "ThemeError");
        SetResourceReference(SpeakingBrushProperty, "ThemeSpeaking");
        Loaded += (_, _) => CompositionTarget.Rendering += RenderFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= RenderFrame;
        IsHitTestVisible = false;
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (!IsVisible || ReduceMotion || e is not RenderingEventArgs args) return;
        var now = args.RenderingTime.TotalSeconds;
        if (now - _last < 1d / 30) return;
        _time += Math.Min(0.1, now - _last); _last = now;
        _smoothed += (Math.Clamp(Level / 100, 0, 1) - _smoothed) * 0.35;
        if (Phase != AssistantPhase.Idle || _smoothed > 0.001) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth; var height = ActualHeight;
        if (width < 1 || height < 1) return;
        var brushProperty = Phase switch { AssistantPhase.Speaking => SpeakingBrushProperty, AssistantPhase.Fault => ErrorBrushProperty,
            AssistantPhase.Idle => IdleBrushProperty, _ => AccentBrushProperty };
        var color = ((SolidColorBrush)GetValue(brushProperty)).Color;
        var center = new Point(width / 2, height / 2);
        var glow = new RadialGradientBrush(Color.FromArgb(22, color.R, color.G, color.B), Colors.Transparent);
        dc.DrawEllipse(glow, null, center, width * 0.44, height * 0.48);
        var phase = ReduceMotion ? 0 : _time;
        var volume = ReduceMotion ? Math.Clamp(Level / 100, 0, 1) : _smoothed;
        var thinking = Phase is AssistantPhase.Loading or AssistantPhase.Recognizing or AssistantPhase.Executing or AssistantPhase.Synthesizing;
        var amplitude = Phase == AssistantPhase.Idle ? 2 : 5 + volume * height * 0.34 + (thinking ? 7 : 0);
        for (int layer = 0; layer < 3; layer++)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                for (int i = 0; i <= 160; i++)
                {
                    double x = i / 160d;
                    var envelope = Math.Pow(Math.Sin(Math.PI * x), 1.8);
                    var wave = Math.Sin(x * 22 + phase * (3 + layer * 0.4)) * 0.65 + Math.Sin(x * 43 - phase * 2.1 + layer) * 0.35;
                    var point = new Point(x * width, center.Y + envelope * amplitude * wave * (1 - layer * 0.2));
                    if (i == 0) path.BeginFigure(point, false, false); else path.LineTo(point, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(220 - layer * 65), color.R, color.G, color.B)), layer == 0 ? 2.3 : 1.2), geometry);
        }
    }
}
