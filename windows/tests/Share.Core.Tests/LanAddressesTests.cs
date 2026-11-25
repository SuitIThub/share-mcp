using System.Net;

namespace Share.Core.Tests;

public class LanAddressesTests
{
    private static NetIf N(string name, string ip) => new(name, IPAddress.Parse(ip));

    [Fact]
    public void FilterKeepsOnlyPrivateLanAddresses()
    {
        var got = LanAddresses.Filter(
            [
                N("WLAN Intel(R) Wi-Fi 6", "192.168.1.5"), N("Ethernet Realtek", "10.1.1.1"),
                N("vEthernet (WSL) Hyper-V Virtual Ethernet Adapter", "172.20.0.1"), N("Ethernet 2 VirtualBox Host-Only", "192.168.56.1"),
                N("VMware Network Adapter VMnet8", "192.168.80.1"), N("Tailscale Tunnel", "10.64.0.1"), N("wg0 WireGuard Tunnel", "10.9.0.2"),
                N("Ethernet 3 TAP-Windows Adapter V9", "10.8.0.2"), N("Bluetooth-Netzwerkverbindung Bluetooth Device", "192.168.44.1"), N("Hyper-V Default", "172.21.0.1"),
                N("OpenVPN Data Channel Offload", "10.8.0.6"), N("ProtonVPN TUN", "10.2.0.2"),
                N("WLAN", "8.8.8.8"), N("Loopback", "127.0.0.1"), N("Ethernet", "172.32.0.1"), N("WLAN", "fe80::1"), N("WLAN", "169.254.3.4"),
            ],
            47800);
        Assert.Equal(["192.168.1.5:47800", "10.1.1.1:47800"], got);
    }

    [Fact]
    public void AcceptsWhole172Block() =>
        Assert.Equal(["172.16.0.1:1", "172.31.255.254:1"], LanAddresses.Filter([N("e", "172.16.0.1"), N("e", "172.31.255.254"), N("e", "172.15.0.1")], 1));

    [Fact]
    public void CandidatesNeverThrow() => Assert.All(LanAddresses.Candidates(47800), c => Assert.EndsWith(":47800", c));
}
