using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;
public class InstallationLayoutTests
{
    [Fact]
    public void InstalledLayoutWinsOverAnAncestorCheckoutAndSeparatesPrivateData()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-layout-" + Guid.NewGuid());
        var app = Path.Combine(root, "Программы с пробелом", "JARVIS");
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "");
            var development = InstallationLayout.Resolve(app, Path.Combine(root, "profile"));
            Assert.False(development.Installed); Assert.Equal(root, development.UserRoot);
            File.WriteAllText(Path.Combine(app, InstallationLayout.Marker), "{}");
            var installed = InstallationLayout.Resolve(app, Path.Combine(root, "profile"));
            Assert.True(installed.Installed); Assert.Equal(app, installed.Assets);
            Assert.Equal(Path.Combine(root, "profile", "JARVIS"), installed.UserRoot);
            Assert.Equal(app, InstallationLayout.AssetRoot(app, root));
            Assert.False(Directory.Exists(installed.UserRoot));
        }
        finally { Directory.Delete(root, true); }
    }
}
