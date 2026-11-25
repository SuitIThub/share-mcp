using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Automation;

namespace Share.Ui.Tests;

/// <summary>The whole program on the real desktop against a real server.</summary>
public sealed class ProgramTest : IDisposable
{
    private readonly GoServer _go = new();
    private readonly TempDir _tmp = new();
    private readonly List<Process> _started = [];
    private readonly string _shots;
    private readonly string _exe;

    public ProgramTest()
    {
        var windows = Path.GetFullPath(Path.Combine(SourceDir(), "..", ".."));
        _exe = Path.Combine(windows, "dist", "loose", "Share.exe");
        Assert.True(File.Exists(_exe), "run packaging/build.ps1 -Loose first");
        _shots = Directory.CreateDirectory(Path.Combine(windows, "tests", "bin", "shots")).FullName;
    }

    private static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    public void Dispose()
    {
        foreach (var p in _started.Where(p => !p.HasExited))
        {
            p.Kill();
            p.WaitForExit();
        }
        _go.Dispose();
        _tmp.Dispose();
    }

    private Process Run(params string[] args)
    {
        var psi = new ProcessStartInfo(_exe) { UseShellExecute = false };
        foreach (var a in (string[])["--data-dir", _tmp.File("data"), "--allow-local-http", .. args]) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    private Process Cli(params string[] args)
    {
        var psi = new ProcessStartInfo(GoServer.ShareExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in (string[])["-config", _tmp.File("cli.json"), .. args]) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static AutomationElement Find(AutomationElement root, Condition c, string what, int seconds = 20)
    {
        for (var end = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < end; Thread.Sleep(150))
            if (root.FindFirst(TreeScope.Descendants, c) is { } e)
                return e;
        throw new TimeoutException("not found: " + what);
    }

    private static AutomationElement Id(AutomationElement root, string id) => Find(root, new PropertyCondition(AutomationElement.AutomationIdProperty, id), id);
    private static AutomationElement Named(AutomationElement root, string name, int seconds = 20) => Find(root, new PropertyCondition(AutomationElement.NameProperty, name), name, seconds);

    private AutomationElement Window(string title) => Find(AutomationElement.RootElement, new AndCondition(
        new PropertyCondition(AutomationElement.NameProperty, title), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
        new PropertyCondition(AutomationElement.ProcessIdProperty, _started[0].Id)), title);

    private static void Click(AutomationElement e) => ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    private static void Type(AutomationElement e, string s) => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(s);

    private void Shot(AutomationElement w, string name)
    {
        Thread.Sleep(300);
        using var img = FlaUI.Core.Capturing.Capture.Rectangle(new System.Drawing.Rectangle((int)w.Current.BoundingRectangle.X, (int)w.Current.BoundingRectangle.Y, (int)w.Current.BoundingRectangle.Width, (int)w.Current.BoundingRectangle.Height));
        img.ToFile(Path.Combine(_shots, name + ".png"));
    }

    [Fact]
    public async Task Walkthrough()
    {
        // pair
        Run();
        var main = Window("Share");
        Named(main, "nicht gekoppelt");
        Shot(main, "1-leer");
        Click(Id(main, "pairNow"));
        var pair = Window("Mit Server koppeln");
        Type(Id(pair, "url"), _go.Url);
        Type(Id(pair, "code"), "ZZZZZZ");
        Type(Id(pair, "name"), "Büro-PC");
        Click(Id(pair, "pair"));
        Named(pair, "Code ungültig oder abgelaufen");
        Shot(pair, "2-koppeln");
        Type(Id(pair, "code"), _go.PairCode());
        Click(Id(pair, "pair"));
        Named(main, "verbunden");

        // receive a file from the agent's CLI
        using (var p = Cli("pair", "-name", "Bot", _go.Url, _go.PairCode())) await p.WaitForExitAsync();
        var data = TempDir.Random(5_000_000);
        using var send = Cli("send", _tmp.File("bericht ü.pdf", data), "--to", "büro");
        Named(main, "Datei von Bot");
        Shot(main, "3-anfrage");
        Click(Named(main, "Annehmen"));
        Named(main, "Im Download-Ordner");
        Shot(main, "4-fertig");
        await send.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, send.ExitCode);
        Assert.Equal(data, File.ReadAllBytes(_tmp.File("data\\Downloads\\bericht ü.pdf")));
        Click(Named(main, "Schließen"));

        // accept a text
        using (var p = Cli("text", "Hallo vom Agenten", "--to", "büro")) await p.WaitForExitAsync();
        Named(main, "Text von Bot");
        Click(Named(main, "Annehmen"));
        Named(main, "In die Zwischenablage kopiert");
        Named(main, "Kopieren");

        // a second device to send to
        using var peer = new Engine(new Api(await _go.AccountAsync("Handy")), new DiskDownloads(_tmp.File("handy"), _tmp.File("handy.json")), new EngineOptions { Port = 0, Candidates = _ => [] });
        peer.Start();
        await Eventually.True(() => peer.State.Connected, "peer");

        // "Senden an": a second start hands over to the first and ends
        using (var second = Run("--send", _tmp.File("foto.jpg", data), _tmp.Path))
        {
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, second.ExitCode);
        }
        var sw = Window("Share – senden");
        Named(sw, "foto.jpg (4,8 MB) senden");
        var texts = sw.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>();
        Assert.Contains(texts, t => t.Current.Name.Contains("Ordner können nicht gesendet werden"));
        Assert.False(Id(sw, "sendNow").Current.IsEnabled);
        ((TogglePattern)Named(sw, "Handy", 30).GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Shot(sw, "5-senden");
        Click(Id(sw, "sendNow"));
        await Eventually.True(() => peer.State.Incoming.Count == 1, "offered");
        await peer.AcceptAsync(peer.State.Incoming[0].Id);
        await Eventually.True(() => peer.State.Downloads.Count == 1 && peer.State.Downloads[0].Phase == Phase.Done, "received", 40);
        Assert.Equal(data, File.ReadAllBytes(peer.State.Downloads[0].Refs.Single()));
        Named(main, "Handy: zugestellt");
        Shot(main, "6-gesendet");

        // settings
        Click(Id(main, "settings"));
        var settings = Window("Einstellungen");
        Click(Id(settings, "addDevice"));
        var code = ((ValuePattern)Id(settings, "newCode").GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
        Assert.Matches("^[A-Z2-9]{6}$", code);
        Shot(settings, "7-einstellungen");
        ((WindowPattern)settings.GetCurrentPattern(WindowPattern.Pattern)).Close();

        // closing hides; another start brings it back; pairing survives a restart
        ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();
        await Task.Delay(800);
        Assert.False(_started[0].HasExited);
        using (var again = Run()) await again.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Named(Window("Share"), "verbunden");
        _started[0].Kill();
        await _started[0].WaitForExitAsync();
        _started.Clear();
        Run();
        Named(Window("Share"), "verbunden");
        using var log = new StreamReader(new FileStream(_tmp.File("data\\share.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Assert.DoesNotContain("Error", await log.ReadToEndAsync());
    }
}
