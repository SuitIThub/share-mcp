package tlsid

import (
	"crypto/tls"
	"crypto/x509"
	"encoding/pem"
	"net"
	"testing"
)

func serve(t *testing.T, certPEM, keyPEM []byte) string {
	cfg, err := ServerConfig(certPEM, keyPEM)
	if err != nil {
		t.Fatal(err)
	}
	ln, err := tls.Listen("tcp", "127.0.0.1:0", cfg)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { ln.Close() })
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			c.(*tls.Conn).Handshake()
			c.Close()
		}
	}()
	return ln.Addr().String()
}

func fp(t *testing.T, certPEM []byte) string {
	b, _ := pem.Decode(certPEM)
	if _, err := x509.ParseCertificate(b.Bytes); err != nil {
		t.Fatal(err)
	}
	return Fingerprint(b.Bytes)
}

func TestPinnedAcceptsOwnCertRejectsOther(t *testing.T) {
	ca, ka, err := Generate("A")
	if err != nil {
		t.Fatal(err)
	}
	cb, _, _ := Generate("B")
	addr := serve(t, ca, ka)
	c, err := tls.Dial("tcp", addr, PinnedClientConfig(fp(t, ca)))
	if err != nil {
		t.Fatal("own cert rejected:", err)
	}
	c.Close()
	if c, err := tls.Dial("tcp", addr, PinnedClientConfig(fp(t, cb))); err == nil {
		c.Close()
		t.Fatal("other cert accepted")
	}
	if len(fp(t, ca)) != 64 {
		t.Fatal("fingerprint must be 64 hex chars")
	}
	_ = net.IPv4len
}

func TestFingerprintFromPEM(t *testing.T) {
	c, _, _ := Generate("A")
	got, err := FingerprintPEM(c)
	if err != nil || got != fp(t, c) {
		t.Fatal(got, err)
	}
}
