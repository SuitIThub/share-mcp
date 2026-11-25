using System.Text;
using Share.Core;

namespace Share.App;

public sealed class PairModel(AppHost host) : Observable
{
    private const int MaxNameBytes = 64;
    private string _url = "", _code = "", _name = Environment.MachineName, _error = "";
    private bool _busy;

    public string Url
    {
        get => _url;
        set
        {
            if (Fill(value)) return;
            Set(ref _url, value);
            Raise(nameof(CanPair));
        }
    }

    public string Code
    {
        get => _code;
        set
        {
            if (Fill(value)) return;
            Set(ref _code, value);
            Raise(nameof(CanPair));
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            Set(ref _name, value);
            Raise(nameof(CanPair));
        }
    }

    public string Error { get => _error; private set => Set(ref _error, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) Raise(nameof(CanPair));
        }
    }

    public bool CanPair => !Busy && !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Code) && !string.IsNullOrWhiteSpace(Name);

    /// <summary>A pairing link fills in both fields; nothing happens before the user clicks.</summary>
    public bool Fill(string link)
    {
        if (PairUri.Parse(link) is not { } u) return false;
        Set(ref _url, u.Url, nameof(Url));
        Set(ref _code, u.Code, nameof(Code));
        Raise(nameof(CanPair));
        return true;
    }

    internal static string CutName(string name)
    {
        var s = name.Trim();
        while (Encoding.UTF8.GetByteCount(s) > MaxNameBytes) s = s[..^(s.Length > 1 && char.IsLowSurrogate(s[^1]) ? 2 : 1)];
        return s;
    }

    public async Task<bool> PairAsync()
    {
        if (!CanPair) return false;
        Busy = true;
        Error = "";
        var (url, code, name) = (Url, Code, CutName(Name));
        try
        {
            // not bound to the window: once the code is used the account must be stored
            var acc = await Task.Run(() => Pairing.PairAsync(url, code, name, Identity.Generate(name), host.AllowLocalHttp)).ConfigureAwait(true);
            host.SetAccount(acc);
            return true;
        }
        catch (ArgumentException)
        {
            Error = "Adresse ungültig – sie muss mit https:// beginnen";
        }
        catch (ApiException e)
        {
            Error = e.Status switch { 403 => "Code ungültig oder abgelaufen", 409 => "Dieser Gerätename ist schon vergeben", 400 => "Gerätename ungültig", _ => "Server lehnt ab: " + e.Message };
        }
        catch (IOException)
        {
            Error = "Server nicht erreichbar";
        }
        finally
        {
            Busy = false;
        }
        return false;
    }
}
