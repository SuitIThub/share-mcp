using System.Windows.Media.Imaging;
using QRCoder;
using Share.Core;

namespace Share.App;

public interface IAutostart
{
    /// <summary>Null: not available (program not installed as a package).</summary>
    Task<bool?> GetAsync();

    /// <summary>Returns the state afterwards; the user may have forbidden it.</summary>
    Task<bool> SetAsync(bool on);
}

public sealed class SettingsModel : Observable
{
    private readonly AppHost _host;
    private readonly IAutostart _autostart;
    private string _name, _message = "", _codeHint = "";
    private string? _newCode;
    private DateTimeOffset _codeExpires;
    private BitmapSource? _qr;
    private bool _autostartAvailable, _autostartOn;

    public SettingsModel(AppHost host, IAutostart autostart)
    {
        (_host, _autostart) = (host, autostart);
        _name = host.DeviceName;
    }

    public string Server => "Server: " + (_host.Engine?.Api.Account.ServerUrl ?? "nicht gekoppelt");
    public bool Paired => _host.Engine is not null;
    public bool NotificationsOff => !_host.NotificationsEnabled;

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value)) Raise(nameof(CanRename));
        }
    }

    public bool CanRename => Paired && !string.IsNullOrWhiteSpace(Name) && Name.Trim() != _host.DeviceName;
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string? NewCode { get => _newCode; private set => Set(ref _newCode, value); }
    public string CodeHint { get => _codeHint; private set => Set(ref _codeHint, value); }
    public BitmapSource? Qr { get => _qr; private set => Set(ref _qr, value); }
    public bool AutostartAvailable { get => _autostartAvailable; private set => Set(ref _autostartAvailable, value); }
    public bool Autostart { get => _autostartOn; private set => Set(ref _autostartOn, value); }

    public async Task LoadAsync()
    {
        var on = await _autostart.GetAsync().ConfigureAwait(true);
        AutostartAvailable = on is not null;
        Autostart = on == true;
    }

    public async Task SetAutostartAsync(bool on)
    {
        Autostart = await _autostart.SetAsync(on).ConfigureAwait(true);
        Raise(nameof(Autostart)); // the check box may have to jump back
        Message = on && !Autostart ? "Der Autostart ist im Task-Manager unter „Autostart von Apps“ abgeschaltet." : "";
    }

    public Task RenameAsync() => TryAsync(async () =>
    {
        await _host.RenameAsync(PairModel.CutName(Name)).ConfigureAwait(true);
        Message = "Gespeichert";
        Raise(nameof(CanRename));
    });

    public Task AddDeviceAsync() => TryAsync(async () =>
    {
        if (_host.Engine is not { } e) return;
        var c = await e.Api.PairCodeAsync().ConfigureAwait(true);
        _codeExpires = c.ExpiresAt;
        Qr = QrImage(new PairUri(e.Api.Account.ServerUrl, c.Code).ToString());
        NewCode = c.Code;
        Tick(DateTimeOffset.UtcNow);
    });

    /// <summary>Called every second while visible.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (NewCode is null) return;
        if (now >= _codeExpires) (NewCode, Qr, CodeHint) = (null, null, "");
        else CodeHint = $"{Format.Remaining(_codeExpires, now)} gültig, nur einmal verwendbar";
    }

    public Task UnpairAsync() => _host.UnpairAsync();

    private async Task TryAsync(Func<Task> f)
    {
        Message = "";
        try
        {
            await f().ConfigureAwait(true);
        }
        catch (ApiException e)
        {
            Message = e.Status == 409 ? "Dieser Gerätename ist schon vergeben" : "Server lehnt ab: " + e.Message;
        }
        catch (IOException)
        {
            Message = "Keine Verbindung zum Server";
        }
    }

    /// <summary>Black on white, whatever the theme, so every camera can read it.</summary>
    internal static BitmapSource QrImage(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var png = new MemoryStream(new PngByteQRCode(data).GetGraphic(8));
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = png;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
