using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;

namespace Jarvis.App.Tests;

public class PowerControlTests
{
    [Theory]
    [InlineData(Intent.ShutdownComputer, "/s")]
    [InlineData(Intent.RestartComputer, "/r")]
    public void WindowsRequestDoesNotForceCloseApplications(Intent intent, string flag)
    {
        // Inspect arguments only. Never start a system power process during tests.
        var request = PowerControl.ShutdownRequest(intent);
        Assert.Equal(System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"), request.FileName);
        Assert.Equal(new[] { flag, "/t", "0" }, request.ArgumentList);
        Assert.False(request.UseShellExecute);
        Assert.True(request.CreateNoWindow);
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerControl.ShutdownRequest(Intent.OpenBrowser));
    }
}
