using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Share.App.Tests;

public sealed class PairModelTests
{
    private static PairModel Model(HostRig r) => new(r.Host);

    private static async Task<PairModel> Try(HostRig r, string url, string code, string name)
    {
        var m = Model(r);
        (m.Url, m.Code, m.Name) = (url, code, name);
        await m.PairAsync();
        return m;
    }

    [Fact]
    public async Task PairsAgainstServer()
    {
        await using var r = await new HostRig().Start(paired: false);
        var m = Model(r);
        Assert.Equal(Environment.MachineName, m.Name);
        (m.Url, m.Code, m.Name) = (r.Go.Url, r.Go.PairCode().ToLowerInvariant(), " Büro ");
        Assert.True(await m.PairAsync());
        Assert.Equal("Büro", r.Host.DeviceName);
        Assert.Contains(await r.Peer.Api.DevicesAsync(), d => d.Name == "Büro" && d.Type == DeviceType.Windows);
    }

    [Fact]
    public async Task ErrorsAreGerman()
    {
        await using var r = await new HostRig().Start(paired: false);
        Assert.Equal("Code ungültig oder abgelaufen", (await Try(r, r.Go.Url, "ZZZZZZ", "X")).Error);
        Assert.Equal("Dieser Gerätename ist schon vergeben", (await Try(r, r.Go.Url, r.Go.PairCode(), "Peer")).Error);
        Assert.Equal("Adresse ungültig – sie muss mit https:// beginnen", (await Try(r, "http://example.org", "ABCDEF", "X")).Error);
        Assert.Equal("Server nicht erreichbar", (await Try(r, "https://127.0.0.1:1", "ABCDEF", "X")).Error);
        Assert.Null(r.Host.Engine);
    }

    [Fact]
    public async Task IncompleteFormCannotBeSent()
    {
        await using var r = await new HostRig().Start(paired: false);
        var m = Model(r);
        Assert.False(m.CanPair);
        (m.Url, m.Code) = ("https://x", "ABC");
        Assert.True(m.CanPair);
        m.Name = " ";
        Assert.False(m.CanPair);
        Assert.False(await m.PairAsync());
    }

    [Fact]
    public async Task PastedLinkFillsTheFieldsOnly()
    {
        await using var r = await new HostRig().Start(paired: false);
        foreach (var into in new Action<PairModel, string>[] { (m, s) => m.Url = s, (m, s) => m.Code = s, (m, s) => m.Fill(s) })
        {
            var m = Model(r);
            into(m, "share://pair?code=ABC123&url=https%3A%2F%2Fx.example");
            Assert.Equal(("https://x.example", "ABC123"), (m.Url, m.Code));
        }
        await Task.Delay(200);
        Assert.Null(r.Host.Engine);
    }

    [Fact]
    public async Task LongNamesAreCutNotRefused()
    {
        await using var r = await new HostRig().Start(paired: false);
        var m = Model(r);
        (m.Url, m.Code, m.Name) = (r.Go.Url, r.Go.PairCode(), new string('ä', 50));
        Assert.True(await m.PairAsync());
        Assert.Equal(new string('ä', 32), r.Host.DeviceName);
    }

    [Fact]
    public async Task RevokedDeviceCanPairAgain()
    {
        await using var r = await new HostRig().Start();
        using (var api = new Api(r.Engine.Api.Account)) await api.DeleteMeAsync();
        await Try(r, r.Go.Url, r.Go.PairCode(), "PC neu");
        Assert.Equal("PC neu", r.Host.DeviceName);
        await r.Until(() => r.Engine.State.Connected, "connected");
    }

    // --- settings ---

    private sealed class FakeAutostart(bool? state) : IAutostart
    {
        public bool? State = state;
        public bool Allow = true;
        public Task<bool?> GetAsync() => Task.FromResult(State);

        public Task<bool> SetAsync(bool on)
        {
            if (Allow) State = on;
            return Task.FromResult(State == true);
        }
    }

    [Fact]
    public async Task AddDeviceShowsWorkingCode()
    {
        await using var r = await new HostRig().Start();
        var m = new SettingsModel(r.Host, new FakeAutostart(null));
        await m.AddDeviceAsync();
        Assert.Matches("^[A-Z2-9]{6}$", m.NewCode);
        Assert.Equal("noch 10 Min gültig, nur einmal verwendbar", m.CodeHint);
        Assert.NotNull(m.Qr);
        var acc = await Pairing.PairAsync(r.Go.Url, m.NewCode!, "Neu", Identity.Generate("n"), allowInsecureLocal: true);
        Assert.NotEmpty(acc.Token);
        m.Tick(DateTimeOffset.UtcNow.AddMinutes(11));
        Assert.Null(m.NewCode); // an expired code must not be shown any longer
        Assert.Null(m.Qr);
    }

    [Fact]
    public void QrContentIsThePairUri()
    {
        var u = new PairUri("https://x.example:8443", "ABC123");
        var bmp = new FormatConvertedBitmap(SettingsModel.QrImage(u.ToString()), PixelFormats.Gray8, null, 0);
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight];
        bmp.CopyPixels(px, bmp.PixelWidth, 0);
        var src = new ZXing.RGBLuminanceSource(px, bmp.PixelWidth, bmp.PixelHeight, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
        var text = new ZXing.QrCode.QRCodeReader().decode(new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(src)))?.Text;
        Assert.Equal(u, PairUri.Parse(text!));
    }

    [Fact]
    public async Task RenameReachesTheServer()
    {
        await using var r = await new HostRig().Start();
        var m = new SettingsModel(r.Host, new FakeAutostart(null));
        Assert.Equal("PC", m.Name);
        Assert.False(m.CanRename);
        m.Name = "Neuer Name";
        Assert.True(m.CanRename);
        await m.RenameAsync();
        Assert.Equal("Gespeichert", m.Message);
        Assert.False(m.CanRename);
        Assert.Contains(await r.Peer.Api.DevicesAsync(), d => d.Name == "Neuer Name");
        m.Name = "peer";
        await m.RenameAsync();
        Assert.Equal("Dieser Gerätename ist schon vergeben", m.Message);
        r.Go.Stop();
        m.Name = "x";
        await m.RenameAsync();
        Assert.Equal("Keine Verbindung zum Server", m.Message);
    }

    [Fact]
    public async Task Autostart()
    {
        await using var r = await new HostRig().Start();
        var none = new SettingsModel(r.Host, new FakeAutostart(null));
        await none.LoadAsync();
        Assert.False(none.AutostartAvailable);
        var a = new FakeAutostart(false);
        var m = new SettingsModel(r.Host, a);
        await m.LoadAsync();
        Assert.True(m.AutostartAvailable);
        Assert.False(m.Autostart);
        await m.SetAutostartAsync(true);
        Assert.True(m.Autostart && a.State == true);
        // the user has forbidden it in the task manager
        (a.Allow, a.State) = (false, false);
        await m.SetAutostartAsync(true);
        Assert.False(m.Autostart);
        Assert.Contains("Task-Manager", m.Message);
    }

    [Fact]
    public async Task NotificationsOffAreMentioned()
    {
        await using var r = await new HostRig().Start();
        Assert.False(new SettingsModel(r.Host, new FakeAutostart(null)).NotificationsOff);
        r.Notifier.Enabled = false;
        Assert.True(new SettingsModel(r.Host, new FakeAutostart(null)).NotificationsOff);
    }
}
