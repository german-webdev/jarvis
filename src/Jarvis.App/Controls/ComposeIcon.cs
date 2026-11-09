using System.Windows;
using System.Windows.Media;

namespace Jarvis.App.Controls;

/// <summary>A shared, resolution-independent sheet and pencil icon.</summary>
public sealed class ComposeIcon : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke),
        typeof(Brush), typeof(ComposeIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    private static readonly Geometry Shape = CreateShape();
    private static Geometry CreateShape()
    {
        var geometry = Geometry.Parse("M 13,3 L 5,3 Q 3,3 3,5 L 3,19 Q 3,21 5,21 L 19,21 Q 21,21 21,19 L 21,15 M 11,13 L 10,17 L 14,16 L 22,8 L 19,5 Z M 17,7 L 20,10 M 7,8 L 10,8 M 7,11 L 8,11");
        geometry.Freeze(); return geometry;
    }
    protected override void OnRender(DrawingContext context)
    {
        var pen = new Pen(Stroke, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var bounds = Shape.GetRenderBounds(pen);
        var scale = Math.Min(ActualWidth / bounds.Width, ActualHeight / bounds.Height);
        if (scale <= 0) return;
        context.PushTransform(new TranslateTransform((ActualWidth - bounds.Width * scale) / 2,
            (ActualHeight - bounds.Height * scale) / 2));
        context.PushTransform(new ScaleTransform(scale, scale));
        context.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
        context.DrawGeometry(null, pen, Shape); context.Pop(); context.Pop(); context.Pop();
    }
}
