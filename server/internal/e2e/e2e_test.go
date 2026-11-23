// Package e2e runs complete scenarios: real server, real senders and receivers.
package e2e

import (
	"bytes"
	"context"
	"crypto/rand"
	"fmt"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"share/internal/client"
	"share/internal/protocol"
	"share/internal/server"
	"share/internal/store"
)

type clock struct {
	mu sync.Mutex
	t  time.Time
}

func (c *clock) Now() time.Time      { c.mu.Lock(); defer c.mu.Unlock(); return c.t }
func (c *clock) Add(d time.Duration) { c.mu.Lock(); c.t = c.t.Add(d); c.mu.Unlock() }

type world struct {
	t     *testing.T
	ts    *httptest.Server
	st    *store.Store
	srv   *server.Server
	clock *clock
	ctx   context.Context
}

func newWorld(t *testing.T) *world {
	st, err := store.Open(":memory:")
	if err != nil {
		t.Fatal(err)
	}
	c := &clock{t: time.Now()}
	srv := server.New(st, server.Config{Now: c.Now})
	ts := httptest.NewServer(srv.Handler())
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(func() { cancel(); ts.Close(); srv.Close(); st.Close() })
	return &world{t: t, ts: ts, st: st, srv: srv, clock: c, ctx: ctx}
}

func (w *world) device(name string, typ protocol.DeviceType) *client.API {
	code, _ := w.st.CreatePairCode(w.clock.Now())
	cfg, err := client.Pair(w.ctx, w.ts.URL, code, name, typ)
	if err != nil {
		w.t.Fatal(err)
	}
	return client.NewAPI(cfg)
}

func (w *world) sender(api *client.API, lan bool) *client.Sender {
	cands := func(port int) []string { return nil }
	if lan {
		cands = func(port int) []string { return []string{fmt.Sprintf("127.0.0.1:%d", port)} }
	}
	s := client.NewSender(api, client.SenderOptions{Port: -1, Candidates: cands})
	go s.Run(w.ctx)
	if err := s.WaitReady(w.ctx, 3*time.Second); err != nil {
		w.t.Fatal(err)
	}
	return s
}

func randomFile(t *testing.T, size int) (string, []byte) {
	data := make([]byte, size)
	rand.Read(data)
	p := filepath.Join(t.TempDir(), "big.bin")
	os.WriteFile(p, data, 0644)
	return p, data
}

func incoming(t *testing.T, api *client.API, id string) protocol.Transfer {
	t.Helper()
	in, err := api.Incoming(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	for _, tr := range in {
		if tr.ID == id {
			return tr
		}
	}
	t.Fatalf("offer %s not incoming", id)
	return protocol.Transfer{}
}

// Review Focus 4: two devices accept a broadcast at the same time and download in parallel.
func TestBroadcastConcurrentReceivers(t *testing.T) {
	w := newWorld(t)
	agent := w.sender(w.device("Agent", protocol.DeviceAgent), true)
	pixel := w.device("Pixel", protocol.DeviceAndroid)
	desk := w.device("Desk", protocol.DeviceWindows)
	p, data := randomFile(t, 20<<20)
	tr, err := agent.OfferFiles(w.ctx, []string{p}, nil, true, "neue Version", 0)
	if err != nil {
		t.Fatal(err)
	}
	type result struct {
		paths []string
		err   error
		path  protocol.Path
	}
	run := func(api *client.API, probe time.Duration, out chan<- result) {
		r := client.NewReceiver(api, t.TempDir())
		r.SetProbeTimeout(probe)
		var mu sync.Mutex
		var last protocol.Path
		paths, err := r.Accept(w.ctx, incoming(t, api, tr.ID), func(_ int64, p protocol.Path) { mu.Lock(); last = p; mu.Unlock() })
		mu.Lock()
		defer mu.Unlock()
		out <- result{paths, err, last}
	}
	a, b := make(chan result, 1), make(chan result, 1)
	go run(pixel, time.Second, a)
	go run(desk, time.Nanosecond, b) // probe always times out -> relay
	ra, rb := <-a, <-b
	for _, r := range []result{ra, rb} {
		if r.err != nil {
			t.Fatal(r.err)
		}
		got, _ := os.ReadFile(r.paths[0])
		if !bytes.Equal(got, data) {
			t.Fatal("content mismatch")
		}
	}
	if ra.path != protocol.PathLAN || rb.path != protocol.PathRelay {
		t.Fatal(ra.path, rb.path)
	}
	st, _ := agent.API().Transfer(w.ctx, tr.ID)
	for _, d := range st.Deliveries {
		if d.Status != protocol.StatusDelivered {
			t.Fatal(st.Deliveries)
		}
	}
	if st.Closed {
		t.Fatal("broadcast closed before expiry")
	}
}

func TestTargetedLifecycle(t *testing.T) {
	w := newWorld(t)
	deskAPI := w.device("Desk", protocol.DeviceWindows)
	desk := w.sender(deskAPI, true)
	pixel := w.device("Pixel", protocol.DeviceAndroid)
	p, data := randomFile(t, 1<<20)
	tr, _ := desk.OfferFiles(w.ctx, []string{p}, []string{"pixel"}, false, "", 0)
	paths, err := client.NewReceiver(pixel, t.TempDir()).Accept(w.ctx, incoming(t, pixel, tr.ID), nil)
	if err != nil {
		t.Fatal(err)
	}
	got, _ := os.ReadFile(paths[0])
	if !bytes.Equal(got, data) {
		t.Fatal("mismatch")
	}
	timeout := time.After(3 * time.Second)
	for {
		select {
		case u := <-desk.Updates():
			if u.TransferID == tr.ID && u.Closed {
				if len(desk.Active()) != 0 {
					t.Fatal("offer still active")
				}
				return
			}
		case <-timeout:
			t.Fatal("no transfer.closed for sender")
		}
	}
}

func TestCancelDuringDownload(t *testing.T) {
	w := newWorld(t)
	desk := w.sender(w.device("Desk", protocol.DeviceWindows), false) // relay keeps it slower
	pixel := w.device("Pixel", protocol.DeviceAndroid)
	p, _ := randomFile(t, 32<<20)
	tr, _ := desk.OfferFiles(w.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	dir := t.TempDir()
	var once sync.Once
	_, err := client.NewReceiver(pixel, dir).Accept(w.ctx, incoming(t, pixel, tr.ID), func(b int64, _ protocol.Path) {
		if b > 1<<20 {
			once.Do(func() { go desk.Cancel(w.ctx, tr.ID) })
		}
	})
	if err == nil {
		t.Fatal("download succeeded despite cancel")
	}
	entries, _ := os.ReadDir(dir)
	for _, e := range entries {
		if !strings.HasSuffix(e.Name(), ".part") {
			t.Fatal("unexpected finished file", e.Name())
		}
	}
}

func TestDeclineAndMissed(t *testing.T) {
	w := newWorld(t)
	desk := w.sender(w.device("Desk", protocol.DeviceWindows), true)
	pixel := w.device("Pixel", protocol.DeviceAndroid)
	tablet := w.device("Tablet", protocol.DeviceAndroid)
	p, _ := randomFile(t, 1000)
	tr, _ := desk.OfferFiles(w.ctx, []string{p}, []string{"Pixel", "Tablet"}, false, "", 0)
	if err := client.NewReceiver(pixel, "").Decline(w.ctx, tr.ID); err != nil {
		t.Fatal(err)
	}
	w.clock.Add(11 * time.Minute)
	w.srv.Tick(w.ctx)
	st, _ := desk.API().Transfer(w.ctx, tr.ID)
	want := map[string]protocol.Status{pixel.Config().DeviceID: protocol.StatusDeclined, tablet.Config().DeviceID: protocol.StatusMissed}
	for _, d := range st.Deliveries {
		if want[d.DeviceID] != d.Status {
			t.Fatal(st.Deliveries)
		}
	}
	if !st.Closed {
		t.Fatal("not closed")
	}
}
