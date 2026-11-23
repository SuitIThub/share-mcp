// Package lanaddr finds this machine's private LAN addresses.
package lanaddr

import (
	"net"
	"strconv"
	"strings"
)

type iface struct {
	name string
	ip   net.IP
}

var excluded = []string{"vethernet", "virtualbox", "vmware", "docker", "tailscale", "zerotier", "wg", "tun", "tap"}

var private = func() []*net.IPNet {
	var out []*net.IPNet
	for _, c := range []string{"10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"} {
		_, n, _ := net.ParseCIDR(c)
		out = append(out, n)
	}
	return out
}()

func filter(ifs []iface, port int) []string {
	var out []string
	for _, f := range ifs {
		name := strings.ToLower(f.name)
		skip := false
		for _, x := range excluded {
			if strings.Contains(name, x) {
				skip = true
				break
			}
		}
		ip4 := f.ip.To4()
		if skip || ip4 == nil || ip4.IsLoopback() {
			continue
		}
		for _, n := range private {
			if n.Contains(ip4) {
				out = append(out, net.JoinHostPort(ip4.String(), strconv.Itoa(port)))
				break
			}
		}
	}
	return out
}

// Candidates returns "ip:port" for each private IPv4 address on an active, non-virtual interface.
func Candidates(port int) []string {
	nifs, err := net.Interfaces()
	if err != nil {
		return nil
	}
	var ifs []iface
	for _, ni := range nifs {
		if ni.Flags&net.FlagUp == 0 || ni.Flags&net.FlagLoopback != 0 {
			continue
		}
		addrs, _ := ni.Addrs()
		for _, a := range addrs {
			if ipn, ok := a.(*net.IPNet); ok {
				ifs = append(ifs, iface{ni.Name, ipn.IP})
			}
		}
	}
	return filter(ifs, port)
}
