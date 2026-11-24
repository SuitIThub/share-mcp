package client

import (
	"bytes"
	"crypto/sha256"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"share/internal/protocol"
)

type progressLog struct {
	mu    sync.Mutex
	calls []struct {
		bytes int64
		path  protocol.Path
	}
}

func (p *progressLog) fn(b int64, path protocol.Path) {
	p.mu.Lock()
	p.calls = append(p.calls, struct {
		bytes int64
		path  protocol.Path
	}{b, path})
	p.mu.Unlock()
}

func (p *progressLog) paths() map[protocol.Path]bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	m := map[protocol.Path]bool{}
	for _, c := range p.calls {
		m[c.path] = true
	}
	return m
}

func receive(t *testing.T, f *senderFixture, tr protocol.Transfer, prog func(int64, protocol.Path)) ([]string, string) {
	t.Helper()
	dir := t.TempDir()
	r := NewReceiver(f.recv, dir)
	r.SetProbeTimeout(500 * time.Millisecond)
	paths, err := r.Accept(f.ctx, tr, prog)
	if err != nil {
		t.Fatal(err)
	}
	return paths, dir
}

func mustEqualFile(t *testing.T, path string, data []byte) {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(b, data) {
		t.Fatalf("content mismatch: got %d bytes want %d", len(b), len(data))
	}
}

func TestPrefersLAN(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	p, data := writeFile(t, 500000)
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	var log progressLog
	paths, dir := receive(t, f, tr, log.fn)
	if len(paths) != 1 || paths[0] != filepath.Join(dir, "payload.bin") {
		t.Fatal(paths)
	}
	mustEqualFile(t, paths[0], data)
	if ps := log.paths(); !ps[protocol.PathLAN] || ps[protocol.PathRelay] {
		t.Fatal(ps)
	}
	entries, _ := os.ReadDir(dir)
	if len(entries) != 1 {
		t.Fatal("leftover files", entries)
	}
	got, _ := f.sender.api.Transfer(f.ctx, tr.ID)
	if got.Deliveries[0].Status != protocol.StatusDelivered {
		t.Fatal(got.Deliveries)
	}
}

func TestFallsBackToRelay(t *testing.T) {
	f := newSenderFixture(t, func(int) []string { return nil })
	p, data := writeFile(t, 500000)
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	var log progressLog
	paths, _ := receive(t, f, tr, log.fn)
	mustEqualFile(t, paths[0], data)
	if ps := log.paths(); ps[protocol.PathLAN] || !ps[protocol.PathRelay] {
		t.Fatal(ps)
	}
}

func TestUnreachableCandidateFallsBack(t *testing.T) {
	f := newSenderFixture(t, func(int) []string { return []string{"127.0.0.1:1"} })
	p, data := writeFile(t, 1000)
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	start := time.Now()
	var log progressLog
	paths, _ := receive(t, f, tr, log.fn)
	mustEqualFile(t, paths[0], data)
	if time.Since(start) > 3*time.Second || !log.paths()[protocol.PathRelay] {
		t.Fatal(time.Since(start), log.paths())
	}
}

// Review Focus 1: interrupted mid-file, resumed over the other path, byte-identical.
func TestResumeSwitchesPath(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	p, data := writeFile(t, 8<<20)
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	var once sync.Once
	var firstRelay int64 = -1
	var mu sync.Mutex
	prog := func(b int64, path protocol.Path) {
		if path == protocol.PathLAN && b >= 3<<20 {
			once.Do(f.sender.dropLocalEndpoint)
		}
		mu.Lock()
		if path == protocol.PathRelay && firstRelay < 0 {
			firstRelay = b
		}
		mu.Unlock()
	}
	paths, _ := receive(t, f, tr, prog)
	b, _ := os.ReadFile(paths[0])
	if sha256.Sum256(b) != sha256.Sum256(data) {
		t.Fatal("content differs after resume")
	}
	if firstRelay < 3<<20 {
		t.Fatalf("relay did not resume from offset (first relay progress %d)", firstRelay)
	}
}

// Review Focus 3: hostile names stay inside the download dir, conflicts get a suffix.
func TestSanitizedAndUniqueNames(t *testing.T) {
	dir := t.TempDir()
	os.WriteFile(filepath.Join(dir, "evil.txt"), []byte("existing"), 0644)
	r := NewReceiver(nil, dir)
	part, final := r.destPaths("0123456789abcdef", 0, "../evil.txt")
	for _, x := range []string{part, final} {
		if filepath.Dir(x) != dir {
			t.Fatalf("%s escapes %s", x, dir)
		}
	}
	if filepath.Base(final) != "evil (1).txt" {
		t.Fatal(final)
	}
	if !strings.HasSuffix(part, ".part") {
		t.Fatal(part)
	}
	_, final = r.destPaths("0123456789abcdef", 1, `C:\Windows\CON`)
	if filepath.Base(final) != "_CON" {
		t.Fatal(final)
	}
}

func TestMultipleFiles(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	p1, d1 := writeFile(t, 1000)
	p2, d2 := writeFile(t, 2000) // same base name "payload.bin" in another dir
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p1, p2}, []string{"Pixel"}, false, "", 0)
	paths, dir := receive(t, f, tr, nil)
	if len(paths) != 2 || paths[1] != filepath.Join(dir, "payload (1).bin") {
		t.Fatal(paths)
	}
	mustEqualFile(t, paths[0], d1)
	mustEqualFile(t, paths[1], d2)
}

func TestTextAcceptDelivers(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	tr, _ := f.sender.OfferText(f.ctx, protocol.KindText, "hallo welt", []string{"Pixel"}, false)
	in, _ := f.recv.Incoming(f.ctx)
	r := NewReceiver(f.recv, t.TempDir())
	paths, err := r.Accept(f.ctx, in[0], nil)
	if err != nil || paths != nil || in[0].Text != "hallo welt" {
		t.Fatal(paths, err)
	}
	got, _ := f.sender.api.Transfer(f.ctx, tr.ID)
	if got.Deliveries[0].Status != protocol.StatusDelivered {
		t.Fatal(got.Deliveries)
	}
}

func TestReceiverDecline(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	tr, _ := f.sender.OfferText(f.ctx, protocol.KindText, "x", []string{"Pixel"}, false)
	r := NewReceiver(f.recv, t.TempDir())
	if err := r.Decline(f.ctx, tr.ID); err != nil {
		t.Fatal(err)
	}
	in, _ := r.Incoming(f.ctx)
	if len(in) != 0 {
		t.Fatal(in)
	}
}
