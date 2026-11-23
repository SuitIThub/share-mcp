package lanaddr

import (
	"net"
	"reflect"
	"testing"
)

func TestFilter(t *testing.T) {
	in := []iface{
		{"Ethernet", net.ParseIP("192.168.1.5")},
		{"Ethernet", net.ParseIP("fe80::1")},
		{"vEthernet (WSL)", net.ParseIP("172.20.0.1")},
		{"Tailscale", net.ParseIP("100.64.0.2")},
		{"WLAN", net.ParseIP("8.8.8.8")},
		{"wlan0", net.ParseIP("10.0.0.7")},
		{"docker0", net.ParseIP("172.17.0.1")},
		{"VirtualBox Host-Only", net.ParseIP("192.168.56.1")},
		{"lo", net.ParseIP("127.0.0.1")},
	}
	got := filter(in, 47800)
	want := []string{"192.168.1.5:47800", "10.0.0.7:47800"}
	if !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
}
