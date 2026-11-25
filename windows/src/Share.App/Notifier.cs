using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Toolkit.Uwp.Notifications;
using Share.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Share.App;

/// <summary>The arguments behind a notification's buttons.</summary>
public static class ToastArgs
{
    public static string Build(Command c) => c switch
    {
        Command.Accept a => $"action=accept;id={Uri.EscapeDataString(a.Id)}",
        Command.Decline d => $"action=decline;id={Uri.EscapeDataString(d.Id)}",
        Command.OpenDownload o => $"action=open;id={Uri.EscapeDataString(o.Id)}",
        Command.OpenLink l => $"action=link;url={Uri.EscapeDataString(l.Url)}",
        _ => "action=show",
    };

    /// <summary>Null for anything unknown.</summary>
    public static Command? Parse(string args)
    {
        var p = new Dictionary<string, string>();
        foreach (var part in args.Split(';'))
            if (part.IndexOf('=') is > 0 and var i)
                p[part[..i]] = Uri.UnescapeDataString(part[(i + 1)..]);
        var id = p.GetValueOrDefault("id", "");
        return (p.GetValueOrDefault("action"), id.Length > 0) switch
        {
            ("show", _) => new Command.Show(),
            ("accept", true) => new Command.Accept(id),
            ("decline", true) => new Command.Decline(id),
            ("open", true) => new Command.OpenDownload(id),
            ("link", _) when Format.IsWebLink(p.GetValueOrDefault("url", "")) => new Command.OpenLink(p["url"]),
            _ => null,
        };
    }
}

/// <summary>Contents of the notifications.</summary>
public static class ToastXml
{
    /// <summary>A toast's XML is limited to 5 KB.</summary>
    private const int MaxLink = 1500;

    public static string Offer(Transfer t, string sender) =>
        Toast(Format.OfferTitle(t, sender), Format.OfferBody(t), ("Annehmen", new Command.Accept(t.Id)), ("Ablehnen", new Command.Decline(t.Id)));

    public static string Result(Download d)
    {
        var name = Format.ShortName(d.Transfer);
        if (d.Phase != Phase.Done) return Toast($"{name} fehlgeschlagen", d.Error ?? "");
        var info = Format.Size(d.Transfer.TotalSize) + (d.Path is null ? "" : " · " + Format.Path(d.Path));
        return Toast($"{name} empfangen", info, (d.Refs.Count > 1 || Results.IsDangerous(d) ? "Im Ordner zeigen" : "Öffnen", new Command.OpenDownload(d.Transfer.Id)));
    }

    public static string Text(Transfer t, string sender)
    {
        var open = t.Kind == Kind.Link && t.Text.Length <= MaxLink && Format.IsWebLink(t.Text);
        (string, Command)[] buttons = open ? [("Öffnen", new Command.OpenLink(t.Text))] : [];
        return Toast($"{(t.Kind == Kind.Link ? "Link" : "Text")} von {sender} kopiert", Format.OfferBody(t), buttons);
    }

    public static string Ended(Transfer t, Func<string, string> name) =>
        Toast($"{Format.ShortName(t)}: Angebot beendet", string.Join("\n", t.Deliveries.Select(d => $"{name(d.DeviceId)}: {Format.Status(d)}")));

    public static string Message(string title, string text) => Toast(title, text);

    private static string Toast(string title, string text, params (string Label, Command Command)[] buttons) =>
        new XElement("toast", new XAttribute("launch", ToastArgs.Build(new Command.Show())),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", Clean(title)), text.Length == 0 ? null : new XElement("text", Clean(text)))),
            new XElement("actions", buttons.Select(b => new XElement("action",
                new XAttribute("content", b.Label), new XAttribute("arguments", ToastArgs.Build(b.Command)), new XAttribute("activationType", "background"))))).ToString(SaveOptions.DisableFormatting);

    /// <summary>Characters XML cannot carry would make the whole notification fail.</summary>
    private static string Clean(string s)
    {
        var b = new StringBuilder();
        foreach (var r in s.EnumerateRunes())
            b.Append(r.Value is '\n' or '\t' || r.Value >= ' ' && r.Value is not (0xFFFE or 0xFFFF or 0xFFFD) ? r.ToString() : " ");
        return b.ToString();
    }
}

/// <summary>The part that talks to Windows.</summary>
public interface IToasts
{
    bool Enabled { get; }
    void Show(string xml, string tag, DateTimeOffset? expires);
    void Remove(string tag);
}

public sealed class WindowsToasts : IToasts
{
    private const string Group = "share";

    public bool Enabled => ToastNotificationManagerCompat.CreateToastNotifier().Setting == NotificationSetting.Enabled;

    public void Show(string xml, string tag, DateTimeOffset? expires)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        ToastNotificationManagerCompat.CreateToastNotifier().Show(new ToastNotification(doc) { Tag = tag, Group = Group, ExpirationTime = expires });
    }

    public void Remove(string tag) => ToastNotificationManagerCompat.History.Remove(tag, Group);
}

public sealed class ToastNotifier(IToasts toasts) : INotifier
{
    public bool Enabled
    {
        get
        {
            try
            {
                return toasts.Enabled;
            }
            catch (Exception e)
            {
                Trace.TraceWarning("notifications unavailable: " + e.Message);
                return false;
            }
        }
    }

    public void Offer(Transfer t, string sender)
    {
        if (t.ExpiresAt > DateTimeOffset.UtcNow) Show(ToastXml.Offer(t, sender), "o:" + t.Id, t.ExpiresAt);
    }

    public void RemoveOffer(string id) => Try(() => toasts.Remove("o:" + id));
    public void Result(Download d) => Show(ToastXml.Result(d), "r:" + d.Transfer.Id);
    public void Text(Transfer t, string sender) => Show(ToastXml.Text(t, sender), "r:" + t.Id);
    public void Ended(Transfer t, Func<string, string> name) => Show(ToastXml.Ended(t, name), "e:" + t.Id);

    public void Message(string title, string text) =>
        Show(ToastXml.Message(title, text), "m:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title + "\0" + text)))[..32]);

    private void Show(string xml, string tag, DateTimeOffset? expires = null) => Try(() => toasts.Show(xml, tag, expires));

    /// <summary>Notifications are a convenience; everything is also in the window and on the icon.</summary>
    private static void Try(Action a)
    {
        try
        {
            a();
        }
        catch (Exception e)
        {
            Trace.TraceWarning("notification failed: " + e.Message);
        }
    }
}
