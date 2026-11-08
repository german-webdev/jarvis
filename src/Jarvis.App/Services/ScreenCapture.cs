using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Jarvis.Core;

namespace Jarvis.App.Services;

public static class ScreenCapture
{
    public static SkillResult Save(string directory)
    {
        Directory.CreateDirectory(directory);
        // Physical virtual desktop coordinates, including monitors left/above the primary display.
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Экран недоступен.");
        var source = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(source);
        var bitmap = CreateCompatibleBitmap(source, width, height);
        IntPtr previous = IntPtr.Zero;
        try
        {
            if (source == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new Win32Exception();
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, source, x, y, 0x40CC0020)) throw new Win32Exception();
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            var path = Path.Combine(directory, $"Jarvis-{DateTime.Now:yyyy-MM-dd-HH-mm-ss-fff}-{Guid.NewGuid().ToString("N")[..6]}.png");
            using (var file = new FileStream(path, FileMode.CreateNew)) encoder.Save(file);
            return new(true, "Скриншот сохранён.", path);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (source != IntPtr.Zero) ReleaseDC(IntPtr.Zero, source);
        }
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
}
