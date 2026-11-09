using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Jarvis.App;

/// <summary>Updates shared resources on the UI thread; never changes Windows preferences.</summary>
public static class ThemeManager
{
    private static bool _initialized;
    public static string Preference { get; private set; } = "dark";
    public static string EffectiveTheme { get; private set; } = "dark";
    public static string ColorStyle { get; private set; } = "graphite";
    public static event Action? Changed;

    public static string Resolve(string preference, bool systemIsDark) => preference switch
    {
        "light" => "light",
        "system" => systemIsDark ? "dark" : "light",
        _ => "dark"
    };

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    public static void Apply(string preference, string colorStyle = "graphite")
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted) return;
        app.Dispatcher.VerifyAccess();
        Preference = preference is "system" or "light" or "dark" ? preference : "dark";
        ColorStyle = new Jarvis.Core.AssistantSettings { ColorStyle = colorStyle }.Normalize().ColorStyle;
        EffectiveTheme = Resolve(Preference, Preference == "system" && ReadSystemIsDark());
        // WPF's built-in Fluent templates keep native editing, keyboard and accessibility behavior.
#pragma warning disable WPF0001
        app.ThemeMode = EffectiveTheme == "dark" ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        var dictionaries = app.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Palette.", StringComparison.Ordinal) == true);
        var variant = ColorStyle == "graphite" ? EffectiveTheme : $"{ColorStyle}.{EffectiveTheme}";
        var source = new Uri($"/Jarvis;component/Themes/Palette.{variant}.xaml", UriKind.Relative);
        if (current?.Source != source)
        {
            var palette = new ResourceDictionary { Source = source };
            if (current == null) dictionaries.Insert(0, palette);
            else dictionaries[dictionaries.IndexOf(current)] = palette;
        }
        // Fluent controls use these public brush keys; keep their accent consistent with JARVIS.
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush" })
            app.Resources[key] = app.Resources["ThemeAccent"];
        foreach (var key in new[] { "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush" })
            app.Resources[key] = app.Resources["ThemeAccentInk"];
        foreach (var key in new[] { "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush", "TextOnAccentFillColorSelectedTextBrush" })
            app.Resources[key] = app.Resources["ThemeAccentText"];
        foreach (Window window in app.Windows) UpdateTitleBar(window);
        Changed?.Invoke();
    }

    public static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false; // Windows' default app appearance when no preference can be read.
        }
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() => { if (_initialized && Preference == "system") Apply("system", ColorStyle); });
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is Window window) UpdateTitleBar(window);
    }

    private static void UpdateTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = EffectiveTheme == "dark" ? 1 : 0;
        // Unsupported Windows builds retain the native title bar; client colors still work.
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _initialized = false;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
