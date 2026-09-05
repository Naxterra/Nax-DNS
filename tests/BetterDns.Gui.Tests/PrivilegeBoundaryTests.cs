using System.IO;
using System.Security.Principal;
using System.Xml.Linq;
using BetterDns.Gui.Services;
using Xunit;

namespace BetterDns.Gui.Tests;

public sealed class PrivilegeBoundaryTests
{
    [Fact]
    public void VisibleApplicationDoesNotRequireAdministratorPrivileges()
    {
        var manifest = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "UiSource", "app.manifest"));
        var privileges = manifest.Descendants().Single(element => element.Name.LocalName == "requestedExecutionLevel");
        Assert.Equal("asInvoker", (string?)privileges.Attribute("level"));
        Assert.Equal("false", (string?)privileges.Attribute("uiAccess"));
        var application = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "UiSource", "App.xaml"));
        Assert.Null(application.Root!.Attribute("StartupUri")); // The helper must never create a UI window.
    }

    [Fact]
    public async Task BrokerCannotBeStartedWithoutAdministratorApproval()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        Assert.Equal(5, await PrivilegedControlSession.RunBrokerAsync("BetterDNS.Broker." + Guid.NewGuid().ToString("N"), Environment.ProcessId));
    }

    [Theory]
    [InlineData("getState", true)]
    [InlineData("testUpstreams", true)]
    [InlineData("saveConfiguration", true)]
    [InlineData("setActive", true)]
    [InlineData("serviceOperation", true)]
    [InlineData("run", false)]
    [InlineData("powershell.exe", false)]
    [InlineData("writeFile", false)]
    [InlineData("setActive\nserviceOperation", false)]
    public void BrokerHasAnExplicitCommandAllowlist(string command, bool permitted)
    {
        Assert.Equal(permitted, PrivilegedControlSession.IsAllowedCommand(command));
    }
}
