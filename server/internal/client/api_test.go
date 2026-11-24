package client

import (
	"context"
	"net"
	"net/http/httptest"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"share/internal/protocol"
	"share/internal/server"
	"share/internal/store"
)

type env struct {
	ts  *httptest.Server
	st  *store.Store
	srv *server.Server
	ln  *trackingListener
}

// trackingListener records accepted conns so tests can drop them (incl. hijacked websockets).
type trackingListener struct {
	net.Listener
	mu    sync.Mutex
	conns []net.Conn
}

func (l *trackingListener) Accept() (net.Conn, error) {
	c, err := l.Listener.Accept()
	if err == nil {
		l.mu.Lock()
		l.conns = append(l.conns, c)
		l.mu.Unlock()
	}
	return c, err
}

func (l *trackingListener) dropAll() {
	l.mu.Lock()
	defer l.mu.Unlock()
	for _, c := range l.conns {
		c.Close()
	}
	l.conns = nil
}

func newEnv(t *testing.T, mod ...func(*server.Config)) *env {
	t.Helper()
	st, err := store.Open(":memory:")
	if err != nil {
		t.Fatal(err)
	}
	cfg := server.Config{}
	for _, m := range mod {
		m(&cfg)
	}
	srv := server.New(st, cfg)
	ts := httptest.NewUnstartedServer(srv.Handler())
	ln := &trackingListener{Listener: ts.Listener}
	ts.Listener = ln
	ts.Start()
	t.Cleanup(func() { ts.Close(); srv.Close(); st.Close() })
	return &env{ts: ts, st: st, srv: srv, ln: ln}
}

func (e *env) pair(t *testing.T, name string, typ protocol.DeviceType) Config {
	t.Helper()
	code, _ := e.st.CreatePairCode(time.Now())
	cfg, err := Pair(context.Background(), e.ts.URL, code, name, typ)
	if err != nil {
		t.Fatal(err)
	}
	return cfg
}

func TestAPIAgainstServer(t *testing.T) {
	e := newEnv(t)
	sc := e.pair(t, "Desk", protocol.DeviceWindows)
	rc := e.pair(t, "Pixel", protocol.DeviceAndroid)
	if sc.DeviceID == "" || sc.Token == "" || len(sc.CertPEM) == 0 || len(sc.KeyPEM) == 0 {
		t.Fatal(sc)
	}
	sa, ra := NewAPI(sc), NewAPI(rc)
	ctx := context.Background()
	devs, err := sa.Devices(ctx)
	if err != nil || len(devs) != 2 {
		t.Fatal(devs, err)
	}
	if devs[0].CertFingerprint == "" {
		t.Fatal("fingerprint not registered")
	}
	resp, err := sa.CreateTransfer(ctx, protocol.CreateTransferRequest{Kind: protocol.KindText, Text: "hi", Targets: []string{"Pixel"}})
	if err != nil {
		t.Fatal(err)
	}
	in, err := ra.Incoming(ctx)
	if err != nil || len(in) != 1 || in[0].Text != "hi" {
		t.Fatal(in, err)
	}
	if _, err := ra.Accept(ctx, resp.Transfer.ID); err != nil {
		t.Fatal(err)
	}
	tr, err := sa.Transfer(ctx, resp.Transfer.ID)
	if err != nil || tr.Deliveries[0].Status != protocol.StatusDelivered {
		t.Fatal(tr, err)
	}
	_, err = sa.CreateTransfer(ctx, protocol.CreateTransferRequest{Kind: protocol.KindText, Text: "hi", Targets: []string{"Nope"}})
	if ae, ok := err.(*APIError); !ok || ae.Status != 400 {
		t.Fatal(err)
	}
	code, err := sa.CreatePairCode(ctx)
	if err != nil || len(code) != 6 {
		t.Fatal(code, err)
	}
}

func TestEventsReconnect(t *testing.T) {
	e := newEnv(t)
	sc := e.pair(t, "Desk", protocol.DeviceWindows)
	rc := e.pair(t, "Pixel", protocol.DeviceAndroid)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	ch := NewAPI(rc).Events(ctx)
	waitOnline := func() {
		deadline := time.Now().Add(3 * time.Second)
		for time.Now().Before(deadline) {
			devs, _ := NewAPI(sc).Devices(ctx)
			for _, d := range devs {
				if d.Name == "Pixel" && d.Online {
					return
				}
			}
			time.Sleep(20 * time.Millisecond)
		}
		t.Fatal("not online")
	}
	waitOnline()
	e.ln.dropAll() // drop websocket
	time.Sleep(50 * time.Millisecond)
	waitOnline()
	NewAPI(sc).CreateTransfer(ctx, protocol.CreateTransferRequest{Kind: protocol.KindText, Text: "x", Targets: []string{"Pixel"}})
	connects := 0
	for {
		select {
		case ev := <-ch:
			if ev.Type == EvConnected {
				connects++
				continue
			}
			if ev.Type != protocol.EvOfferNew || connects < 2 {
				t.Fatal(ev.Type, connects)
			}
			return
		case <-time.After(3 * time.Second):
			t.Fatal("no event after reconnect")
		}
	}
}

func TestConfigSaveLoad(t *testing.T) {
	p := filepath.Join(t.TempDir(), "sub", "config.json")
	c := Config{ServerURL: "https://x", DeviceID: "d", Token: "t", CertPEM: []byte("c"), KeyPEM: []byte("k")}
	if err := c.Save(p); err != nil {
		t.Fatal(err)
	}
	got, err := LoadConfig(p)
	if err != nil || got.Token != "t" || string(got.KeyPEM) != "k" {
		t.Fatal(got, err)
	}
}
