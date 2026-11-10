namespace Jarvis.Core;

public sealed record InstallationLayout(string Assets, string UserRoot, bool Installed)
{
    public const string Marker = "jarvis.install.json";
    public static InstallationLayout Resolve(string executableDirectory, string localApplicationData)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(executableDirectory));
        if (File.Exists(Path.Combine(directory.FullName, Marker)))
            return new(directory.FullName, Path.Combine(localApplicationData, "JARVIS"), true);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        return directory == null
            ? throw new DirectoryNotFoundException("Не найдены файлы JARVIS. Переустановите приложение.")
            : new(directory.FullName, directory.FullName, false);
    }
    public static string AssetRoot(string executableDirectory, string developmentRoot) =>
        File.Exists(Path.Combine(executableDirectory, Marker)) ? executableDirectory : developmentRoot;
}
