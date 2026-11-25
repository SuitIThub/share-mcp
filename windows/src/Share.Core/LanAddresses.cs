using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Share.Core;

public sealed record NetIf(string Name, IPAddress Address);

public static class LanAddresses
{
    // virtual/VPN adapters; matched against name and description
    private static readonly string[] Excluded =
        ["vethernet", "hyper-v", "virtualbox", "vmware", "docker", "tailscale", "zerotier", "wg", "wireguard", "tun", "tap", "vpn", "bluetooth"];

    /// <summary>"ip:port" for each RFC 1918 IPv4 address on a non-virtual adapter.</summary>
    public static IReadOnlyList<string> Filter(IEnumerable<NetIf> ifs, int port) =>
        [.. ifs.Where(f => !Excluded.Any(x => f.Name.Contains(x, StringComparison.OrdinalIgnoreCase)) && IsPrivate(f.Address)).Select(f => $"{f.Address}:{port}")];

    private static bool IsPrivate(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>LAN candidates of this device.</summary>
    public static IReadOnlyList<string> Candidates(int port)
    {
        try
        {
            var ifs = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => new NetIf($"{n.Name} {n.Description}", u.Address)));
            return Filter(ifs, port);
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
