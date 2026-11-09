using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Jarvis.App.Services;

/// <summary>Positions companion settings in physical pixels, clear of the companion itself.</summary>
public static class CompanionPlacement
{
    public static Rectangle Choose(Rectangle owner, System.Drawing.Size size, IReadOnlyList<Rectangle> workAreas,
        int gap, Rectangle? current = null)
    {
        if (workAreas.Count == 0) throw new ArgumentException("A work area is required.", nameof(workAreas));
        // Retain the dialog while a slider is dragged unless the growing companion reaches it.
        if (current is { } existing && workAreas.Any(area => area.Contains(existing)) && !existing.IntersectsWith(owner)) return existing;
        var candidates = new List<Rectangle>();
        foreach (var area in workAreas)
        {
            var centeredX = owner.Left + (owner.Width - size.Width) / 2;
            var centeredY = owner.Top + (owner.Height - size.Height) / 2;
            foreach (var location in new[] {
                new System.Drawing.Point(centeredX, owner.Bottom + gap),
                new System.Drawing.Point(owner.Right + gap, centeredY),
                new System.Drawing.Point(owner.Left - size.Width - gap, centeredY),
                new System.Drawing.Point(centeredX, owner.Top - size.Height - gap) })
            {
                var candidate = new Rectangle(Math.Clamp(location.X, area.Left, Math.Max(area.Left, area.Right - size.Width)),
                    Math.Clamp(location.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)), size.Width, size.Height);
                candidates.Add(candidate);
                if (area.Contains(candidate) && !candidate.IntersectsWith(owner)) return candidate;
            }
        }
        // A very small work area can physically lack room for both windows. Minimize occlusion.
        return candidates.OrderBy(candidate => Overlap(candidate, owner)).First();
    }
    private static long Overlap(Rectangle left, Rectangle right)
    {
        var overlap = Rectangle.Intersect(left, right);
        return (long)overlap.Width * overlap.Height;
    }

    public static void Attach(Window dialog, Window owner)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        bool pending = false, closed = false;
        void Place(bool initial)
        {
            if (closed) return;
            var handle = new WindowInteropHelper(dialog).Handle;
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            if (handle == 0 || ownerHandle == 0 || !GetWindowRect(handle, out var bounds) || !GetWindowRect(ownerHandle, out var ownerBounds)) return;
            var target = ownerBounds.Rectangle;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(owner);
            if (owner is MiniWindow)
            {
                // Reserve the largest slider size so the settings do not chase the pointer.
                var maximumWidth = Math.Max(target.Width, (int)Math.Ceiling((180 * 1.6 + 48) * dpi.DpiScaleX));
                var maximumHeight = Math.Max(target.Height, (int)Math.Ceiling((200 * 1.6 + 72) * dpi.DpiScaleY));
                target = new Rectangle(target.Left + (target.Width - maximumWidth) / 2,
                    target.Bottom - maximumHeight, maximumWidth, maximumHeight);
            }
            var current = bounds.Rectangle;
            var nearest = Forms.Screen.FromRectangle(target);
            var areas = Forms.Screen.AllScreens.OrderBy(screen => screen.DeviceName == nearest.DeviceName ? 0 : 1).Select(screen => screen.WorkingArea).ToArray();
            var gap = Math.Max(8, (int)Math.Round(10 * dpi.DpiScaleX));
            var next = Choose(target, current.Size, areas, gap, initial ? null : current);
            if (next.Location != current.Location) SetWindowPos(handle, 0, next.X, next.Y, 0, 0, 0x15);
        }
        void Queue()
        {
            if (pending || closed) return;
            pending = true;
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { pending = false; Place(false); }));
        }
        void OnLoaded(object sender, RoutedEventArgs args) => Place(true);
        void OnRendered(object? sender, EventArgs args) => Place(false);
        void OnMoved(object? sender, EventArgs args) => Queue();
        void OnSized(object sender, SizeChangedEventArgs args) => Queue();
        void OnDpi(object sender, DpiChangedEventArgs args) => Queue();
        void OnClosed(object? sender, EventArgs args)
        {
            closed = true;
            owner.LocationChanged -= OnMoved; owner.SizeChanged -= OnSized; owner.DpiChanged -= OnDpi;
            dialog.Loaded -= OnLoaded; dialog.ContentRendered -= OnRendered; dialog.SizeChanged -= OnSized;
            dialog.DpiChanged -= OnDpi; dialog.Closed -= OnClosed;
        }
        owner.LocationChanged += OnMoved; owner.SizeChanged += OnSized; owner.DpiChanged += OnDpi;
        dialog.Loaded += OnLoaded; dialog.ContentRendered += OnRendered; dialog.SizeChanged += OnSized;
        dialog.DpiChanged += OnDpi; dialog.Closed += OnClosed;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle Rectangle => new(Left, Top, Right - Left, Bottom - Top);
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
