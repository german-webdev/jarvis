using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Jarvis.App.Services;

internal sealed class ChromeBrowserConnection(string token, string? executablePath = null, string? userDataDirectory = null)
{
    private const string Prefix = "PLAYWRIGHT_MCP_EXTENSION_TOKEN=";
    public static readonly Uri StorePage = new("https://chromewebstore.google.com/detail/playwright-extension/mmlmfjhmonkocbjadbfplnigmagldckm");
    public static readonly Uri StatusPage = new("chrome-extension://mmlmfjhmonkocbjadbfplnigmagldckm/status.html");
    public const string Unavailable = "Не удалось подключиться к браузеру. Проверьте расширение Playwright и код подключения в «Подключениях ИИ».";
    public string Token { get; } = token;
    private readonly string? _registeredExecutable = executablePath ?? BrowserLauncher.GetDefaultExecutable();
    public string? ExecutablePath => executablePath ?? (Channel == "chromium" ? _registeredExecutable : null);
    public string? UserDataDirectory { get; } = userDataDirectory;
    public string Channel => ChannelFor(_registeredExecutable);

    internal static string ChannelFor(string? executable) => Path.GetFileName(executable)?.ToLowerInvariant() switch
    {
        "chrome.exe" => "chrome",
        "msedge.exe" => "msedge",
        null or "" => throw new InvalidDataException("Не удалось определить браузер по умолчанию. Выберите браузер для HTTPS в Windows."),
        "firefox.exe" => throw new InvalidDataException("Этот браузер не поддерживает подключение через расширение Playwright для Chromium."),
        _ => "chromium"
    };

    public static string NormalizeToken(string value)
    {
        value = value.Trim();
        if (value.StartsWith(Prefix, StringComparison.Ordinal)) value = value[Prefix.Length..].Trim();
        value = value.Trim('"', '\'');
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Проверьте код подключения браузера.");
        return value;
    }

    public static void OpenSetupPage(Uri address)
    {
        var executable = BrowserLauncher.GetDefaultExecutable();
        _ = ChannelFor(executable);
        using var process = Process.Start(new ProcessStartInfo(executable!) { UseShellExecute = false, ArgumentList = { address.AbsoluteUri } });
    }
}
