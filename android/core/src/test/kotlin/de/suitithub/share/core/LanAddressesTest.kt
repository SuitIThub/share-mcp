package de.suitithub.share.core

import org.junit.Assert.assertEquals
import org.junit.Test
import java.net.InetAddress

class LanAddressesTest {
    private fun nif(name: String, ip: String) = NetIf(name, InetAddress.getByName(ip))

    @Test fun filterKeepsOnlyPrivateWifiLikeAddresses() {
        val got = filterLan(
            listOf(
                nif("wlan0", "192.168.1.5"),
                nif("ap0", "10.1.1.1"),
                nif("rmnet_data0", "10.2.3.4"), nif("ccmni0", "10.2.3.5"), nif("tun0", "10.8.0.2"), nif("wg0", "10.9.0.2"),
                nif("v4-rmnet_data0", "192.168.9.9"), nif("ppp0", "10.0.0.9"), nif("dummy0", "10.0.0.8"), nif("clat4", "192.168.0.4"),
                nif("vEthernet (WSL)", "172.20.0.1"), nif("docker0", "172.17.0.1"),
                nif("wlan0", "8.8.8.8"), nif("lo", "127.0.0.1"), nif("eth0", "172.32.0.1"), nif("wlan0", "fe80::1"),
                nif("p2p-wlan0-0", "192.168.49.1"),
            ),
            47800,
        )
        assertEquals(listOf("192.168.1.5:47800", "10.1.1.1:47800", "192.168.49.1:47800"), got)
    }

    @Test fun acceptsWhole172Block() =
        assertEquals(listOf("172.16.0.1:1", "172.31.255.254:1"), filterLan(listOf(nif("eth0", "172.16.0.1"), nif("eth0", "172.31.255.254"), nif("eth0", "172.15.0.1")), 1))
}
