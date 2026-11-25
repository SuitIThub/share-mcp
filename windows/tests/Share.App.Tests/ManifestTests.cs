using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Share.App.Tests;

public sealed class ManifestTests
{
    private static string Windows([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
    private static readonly XDocument M = XDocument.Load(Path.Combine(Windows(), "packaging", "AppxManifest.xml"));

    private static IEnumerable<XElement> All(string name) => M.Descendants().Where(e => e.Name.LocalName == name);
    private static XElement One(string name) => All(name).Single();
    private static XElement Extension(string category) => All("Extension").Single(e => (string?)e.Attribute("Category") == category);

    [Fact]
    public void Identity()
    {
        var id = One("Identity");
        Assert.Equal("SuitIThub.Share", (string?)id.Attribute("Name"));
        Assert.Equal("CN=Share (privat)", (string?)id.Attribute("Publisher"));
        Assert.Contains("\"CN=Share (privat)\"", File.ReadAllText(Path.Combine(Windows(), "packaging", "build.ps1"))); // or Windows refuses the signature
        Assert.Equal("10.0.19041.0", (string?)One("TargetDeviceFamily").Attribute("MinVersion"));
        Assert.Contains(All("Capability"), c => (string?)c.Attribute("Name") == "runFullTrust");
        Assert.Equal("Share.exe", (string?)One("Application").Attribute("Executable"));
    }

    [Fact]
    public void ShareTarget()
    {
        var s = Extension("windows.shareTarget");
        Assert.Single(s.Descendants(), e => e.Name.LocalName == "SupportsAnyFileType");
        Assert.Equal(["Bitmap", "StorageItems", "Text", "WebLink"], s.Descendants().Where(e => e.Name.LocalName == "DataFormat").Select(e => e.Value).Order());
    }

    [Fact]
    public void Startup()
    {
        var t = Extension("windows.startupTask").Elements().Single();
        Assert.Equal(Autostart.TaskId, (string?)t.Attribute("TaskId"));
        Assert.Equal("true", (string?)t.Attribute("Enabled"));
    }

    [Fact]
    public void Firewall()
    {
        var f = One("FirewallRules");
        Assert.Equal("Share.exe", (string?)f.Attribute("Executable"));
        var rule = f.Elements().Single();
        Assert.Equal(("in", "TCP", "private"), ((string?)rule.Attribute("Direction"), (string?)rule.Attribute("IPProtocol"), (string?)rule.Attribute("Profile")));
    }

    [Fact]
    public void ToastActivation()
    {
        var toast = (string?)Extension("windows.toastNotificationActivation").Elements().Single().Attribute("ToastActivatorCLSID");
        var server = One("ExeServer");
        Assert.Equal(toast, (string?)server.Elements().Single().Attribute("Id"));
        Assert.Equal("-ToastActivated", (string?)server.Attribute("Arguments"));
        Assert.True(StartArgs.Parse(["-ToastActivated"]).Background);
    }

    [Fact]
    public void ProtocolAndAlias()
    {
        Assert.Equal("share", (string?)Extension("windows.protocol").Elements().Single().Attribute("Name"));
        Assert.Equal("share-senden.exe", (string?)One("ExecutionAlias").Attribute("Alias"));
    }

    [Fact]
    public void LogosExist()
    {
        foreach (var path in M.Descendants().SelectMany(e => e.Attributes()).Where(a => a.Value.EndsWith(".png")).Select(a => a.Value).Append(One("Logo").Value))
        {
            var dir = Path.Combine(Windows(), "packaging", Path.GetDirectoryName(path)!);
            Assert.NotEmpty(Directory.GetFiles(dir, Path.GetFileNameWithoutExtension(path) + ".scale-*.png"));
        }
    }
}
