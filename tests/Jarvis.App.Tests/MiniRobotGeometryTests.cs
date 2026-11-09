using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Jarvis.App.Controls;
using Jarvis.Core;
using Xunit;
using Path = System.Windows.Shapes.Path;

namespace Jarvis.App.Tests;

public class MiniRobotGeometryTests
{
    [Fact]
    public void ChestStaysConcentricAfterDpiAndRobotSizeChanges()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // No Application or visible window: exercise the real artwork and inherited
                // window rounding, including transitions on an already laid-out visual tree.
                var robot = new MiniRobot { ReduceMotion = true };
                var host = new Border { UseLayoutRounding = true, Child = new Viewbox { Child = robot } };
                Layout(host, new Size(180, 200));
                var ring = (Ellipse)robot.FindName("ChestRing");
                var canvas = (Canvas)VisualTreeHelper.GetParent(ring);
                var backing = canvas.Children.OfType<Ellipse>().Single(ellipse => Canvas.GetLeft(ellipse) == 78);
                var arc = (Path)robot.FindName("ChestArc");
                var mark = (Path)robot.FindName("ChestMark");

                foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2.0, 1.25, 1.0 })
                foreach (var scale in new[] { .8, 1.0, 1.3 })
                foreach (var phase in new[] { AssistantPhase.Idle, AssistantPhase.Recognizing, AssistantPhase.Fault })
                {
                    robot.Phase = phase;
                    VisualTreeHelper.SetRootDpi(host, new DpiScale(dpi, dpi));
                    // A detached test tree has no HwndTarget to invalidate layout on DPI changes.
                    InvalidateLayout(host);
                    Layout(host, new Size(180 * scale, 200 * scale));
                    Assert.Equal(dpi, VisualTreeHelper.GetDpi(ring).DpiScaleX);

                    var context = $"DPI {dpi}, scale {scale}, phase {phase}";
                    var expected = canvas.TransformToAncestor(host).Transform(new Point(90, 153));
                    AssertSamePoint(expected, CenterInHost(backing, host), dpi, context + " backing");
                    AssertSamePoint(expected, CenterInHost(ring, host), dpi, context + " ring");

                    if (phase == AssistantPhase.Recognizing)
                    {
                        var rotation = (RotateTransform)arc.RenderTransform;
                        foreach (var angle in new[] { 0d, 90d, 217d })
                        {
                            rotation.Angle = angle;
                            var pivot = arc.TransformToAncestor(host).Transform(new Point(rotation.CenterX, rotation.CenterY));
                            AssertSamePoint(expected, pivot, dpi, context + $" arc at {angle}");
                        }
                        rotation.Angle = 0;
                    }
                    if (phase == AssistantPhase.Fault)
                    {
                        var markCenter = CenterInHost(mark, host);
                        Assert.True(Math.Abs(markCenter.X - expected.X) * dpi < .000001, context + " mark axis");
                    }
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Point CenterInHost(Shape shape, Visual host)
    {
        var bounds = shape.RenderedGeometry.Bounds;
        return shape.TransformToAncestor(host).Transform(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
    }

    private static void AssertSamePoint(Point expected, Point actual, double dpi, string context)
    {
        Assert.True((expected - actual).Length * dpi < .000001,
            $"{context}: expected {expected}, actual {actual}");
    }

    private static void Layout(FrameworkElement element, Size size)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }

    private static void InvalidateLayout(DependencyObject element)
    {
        if (element is UIElement visual)
        {
            visual.InvalidateMeasure();
            visual.InvalidateArrange();
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            InvalidateLayout(VisualTreeHelper.GetChild(element, index));
    }
}
