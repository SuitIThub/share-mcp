package de.suitithub.share.core

import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

data class NetIf(val name: String, val address: InetAddress)

// virtual/VPN interfaces (as in the Go client) plus Android's mobile data interfaces
private val excluded = listOf(
    "vethernet", "virtualbox", "vmware", "docker", "tailscale", "zerotier", "wg", "tun", "tap",
    "rmnet", "ccmni", "ppp", "clat", "v4-", "dummy",
)

/** "ip:port" for each RFC 1918 IPv4 address on a non-virtual interface. */
fun filterLan(ifs: List<NetIf>, port: Int): List<String> = ifs.filter { f ->
    val name = f.name.lowercase()
    val ip = f.address
    excluded.none { it in name } && ip is Inet4Address && !ip.isLoopbackAddress && ip.isSiteLocalAddress
}.map { "${it.address.hostAddress}:$port" }

/** LAN candidates of this device. */
fun lanCandidates(port: Int): List<String> = try {
    val ifs = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
        .filter { it.isUp && !it.isLoopback }
        // the display name carries the adapter name on Windows
        .flatMap { ni -> ni.inetAddresses.toList().map { NetIf("${ni.name} ${ni.displayName}", it) } }
    filterLan(ifs, port)
} catch (_: java.net.SocketException) {
    emptyList()
}
