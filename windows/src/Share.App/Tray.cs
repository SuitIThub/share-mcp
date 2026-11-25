using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Share.App;

/// <summary>The icon in the notification area; a dot means that offers wait for an answer.</summary>
public sealed class Tray : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly Icon _plain;
    private readonly Icon _dot;

    public Tray(Action open, Action sendFiles, Action quit)
    {
        using var s = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Share;component/share.ico")).Stream;
        _plain = new Icon(s, SystemInformation.SmallIconSize);
        _dot = WithDot(_plain);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => open());
        menu.Items.Add("Dateien senden…", null, (_, _) => sendFiles());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => quit());
        menu.Items[0].Font = new Font(menu.Items[0].Font, System.Drawing.FontStyle.Bold);
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) open();
        };
        Update(0, false);
        _icon.Visible = true;
    }

    public void Update(int waiting, bool connected)
    {
        _icon.Icon = waiting > 0 ? _dot : _plain;
        _icon.Text = waiting > 0 ? $"Share – {waiting} offene Anfrage{(waiting == 1 ? "" : "n")}" : connected ? "Share – verbunden" : "Share – nicht verbunden";
    }

    private static Icon WithDot(Icon icon)
    {
        using var bmp = icon.ToBitmap();
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var d = bmp.Width * 0.5f;
            g.FillEllipse(Brushes.White, bmp.Width - d - 1, -1, d + 2, d + 2);
            g.FillEllipse(Brushes.Red, bmp.Width - d, 0, d, d);
        }
        var h = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(h);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(h);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _plain.Dispose();
        _dot.Dispose();
    }
}
