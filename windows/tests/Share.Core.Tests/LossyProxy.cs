using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Share.Core.Tests;

/// <summary>Forwards connections, but can swallow the answer to one request that did reach the server.</summary>
public sealed class LossyProxy : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly int _target;

    public LossyProxy(string url)
    {
        _target = new Uri(url).Port;
        _l.Start();
        _ = Loop();
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}";
    public volatile string? DropNextAnswerTo;
    public volatile bool Dropped;

    private async Task Loop()
    {
        while (true)
        {
            TcpClient c;
            try
            {
                c = await _l.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }
            _ = Forward(c);
        }
    }

    private async Task Forward(TcpClient c)
    {
        using var client = c;
        using var server = new TcpClient("127.0.0.1", _target);
        var drop = false;
        var up = Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await c.GetStream().ReadAsync(buf)) > 0)
            {
                if (DropNextAnswerTo is { } d && Encoding.Latin1.GetString(buf, 0, n).StartsWith(d, StringComparison.Ordinal))
                {
                    drop = true;
                    DropNextAnswerTo = null;
                }
                await server.GetStream().WriteAsync(buf.AsMemory(0, n));
            }
        });
        var down = Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await server.GetStream().ReadAsync(buf)) > 0)
            {
                if (drop)
                {
                    Dropped = true;
                    return;
                }
                await c.GetStream().WriteAsync(buf.AsMemory(0, n));
            }
        });
        try
        {
            await await Task.WhenAny(up, down);
        }
        catch (Exception)
        {
            // one side hung up
        }
    }

    public void Dispose() => _l.Dispose();
}
