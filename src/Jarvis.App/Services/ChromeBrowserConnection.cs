using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Jarvis.App.Services;

internal sealed class ChromeBrowserConnection(string token, string? executablePath = null, string? userDataDirectory = null)
{
    private const string Prefix = "PLAYWRIGHT_MCP_EXTENSION_TOKEN=";
    public static readonly Uri StorePage = new("https://chromewebstore.google.com/detail/playwright-extension/mmlmfjhmonkocbjadbfplnigmagldckm");
    public static readonly Uri StatusPage = new("chrome-extension://mmlmfjhmonkocbjadbfplnigmagldckm/status.html");
    public const string Unavailable = "Не удалось подключиться к Chrome. Проверь расширение Playwright и код подключения в «Подключениях ИИ».";
    public string Token { get; } = token;
    public string? ExecutablePath { get; } = executablePath;
    public string? UserDataDirectory { get; } = userDataDirectory;

    public static string NormalizeToken(string value)
    {
        value = value.Trim();
        if (value.StartsWith(Prefix, StringComparison.Ordinal)) value = value[Prefix.Length..].Trim();
        value = value.Trim('"', '\'');
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Проверь код подключения Chrome.");
        return value;
    }

    public static void OpenSetupPage(Uri address)
    {
        var registered = BrowserLauncher.GetDefaultExecutable();
        var chrome = string.Equals(Path.GetFileName(registered), "chrome.exe", StringComparison.OrdinalIgnoreCase) ? registered : null;
        chrome ??= new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData }
            .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Google", "Chrome", "Application", "chrome.exe")).FirstOrDefault(File.Exists);
        if (chrome == null) throw new InvalidDataException("Chrome не найден.");
        using var process = Process.Start(new ProcessStartInfo(chrome) { UseShellExecute = false, ArgumentList = { address.AbsoluteUri } });
    }
}
