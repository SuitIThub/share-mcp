package client

import (
	"bytes"
	"os"
	"sync"
	"testing"
	"time"

	"share/internal/protocol"
)

// Final review #1: a transfer.closed missed while disconnected is recovered after reconnect.
func TestSenderResyncsAfterReconnect(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	p, _ := writeFile(t, 1000)
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	f.e.ln.dropAll()
	if err := f.recv.Decline(f.ctx, tr.ID); err != nil { // closes the targeted offer while sender is offline
		t.Fatal(err)
	}
	timeout := time.After(8 * time.Second)
	for {
		select {
		case u := <-f.sender.Updates():
			if u.TransferID == tr.ID && u.Closed {
				if len(f.sender.Active()) != 0 {
					t.Fatal("offer still active")
				}
				return
			}
		case <-timeout:
			t.Fatal("sender never learned that the offer closed")
		}
	}
}

// Final review #2: a short sender outage must not make the receiver give up.
func TestReceiverSurvivesSenderOutage(t *testing.T) {
	f := newSenderFixture(t, func(int) []string { return nil }) // relay only
	p, data := writeFile(t, 8<<20)
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	r := NewReceiver(f.recv, t.TempDir())
	r.backoffBase = 10 * time.Millisecond
	var once sync.Once
	paths, err := r.Accept(f.ctx, tr, func(b int64, _ protocol.Path) {
		if b > 1<<20 {
			once.Do(f.e.ln.dropAll)
		}
	})
	if err != nil {
		t.Fatal(err)
	}
	got, _ := os.ReadFile(paths[0])
	if !bytes.Equal(got, data) {
		t.Fatal("content mismatch")
	}
}

// Final review #5: closing another offer while a new one registers must not kill the LAN endpoint.
func TestEndpointSurvivesConcurrentClose(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	p, data := writeFile(t, 100000)
	a, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	var once sync.Once
	f.sender.beforeCreate = func() { once.Do(func() { f.sender.Cancel(f.ctx, a.ID) }) }
	b, err := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	if err != nil {
		t.Fatal(err)
	}
	var log progressLog
	paths, dir := receive(t, f, b, log.fn)
	_ = dir
	mustEqualFile(t, paths[0], data)
	if ps := log.paths(); !ps[protocol.PathLAN] || ps[protocol.PathRelay] {
		t.Fatal("fell back to relay", ps)
	}
}
