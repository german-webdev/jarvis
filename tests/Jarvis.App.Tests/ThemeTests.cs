using Jarvis.App;
using System.Runtime.ExceptionServices;
using System.IO;
using System.Windows.Markup;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Jarvis.App.Tests;

public class ThemeTests
{
    [Fact]
    public void EveryPaletteKeepsReadableTextAndSeparateRubyErrorColor()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var style in new[] { "graphite", "sapphire", "emerald", "ruby", "amethyst" })
                foreach (var mode in new[] { "light", "dark" })
                {
                    var variant = style == "graphite" ? mode : $"{style}.{mode}";
                    using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Palettes", $"Palette.{variant}.xaml"));
                    var palette = (ResourceDictionary)XamlReader.Load(source);
                    Color Color(string key) => ((SolidColorBrush)palette[key]).Color;
                    void Contrast(string foreground, string background)
                    {
                        var first = Luminance(Color(foreground)); var second = Luminance(Color(background));
                        var ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
                        Assert.True(ratio >= 4.5, $"{variant}: {foreground}/{background} contrast {ratio:F2}:1 is below 4.5:1.");
                    }
                    foreach (var foreground in new[] { "ThemeText", "ThemeMuted", "ThemeAccentInk", "ThemeError" })
                    foreach (var background in new[] { "ThemeWindow", "ThemeSurface", "ThemeRaised", "ThemeHover" })
                        Contrast(foreground, background);
                    Contrast("ThemeAccentText", "ThemeAccent");
                    Contrast("ThemeSelectionText", "ThemeSelection");
                    // The companion visor remains dark even with a light application theme.
                    var visor = System.Windows.Media.Color.FromRgb(0x24, 0x2B, 0x31);
                    Assert.True((Luminance(Color("ThemeCompanionAccent")) + .05) / (Luminance(visor) + .05) >= 3,
                        $"{variant}: companion expression must remain visible against its fixed visor.");
                    if (style == "ruby") Assert.NotEqual(Color("ThemeAccent"), Color("ThemeError"));
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte component) { var value = component / 255.0; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    [Theory]
    [InlineData("system", true, "dark")]
    [InlineData("system", false, "light")]
    [InlineData("dark", false, "dark")]
    [InlineData("light", true, "light")]
    [InlineData("invalid", false, "dark")]
    public void OnlySystemAppearanceFollowsWindows(string preference, bool systemDark, string expected) =>
        Assert.Equal(expected, ThemeManager.Resolve(preference, systemDark));
}
