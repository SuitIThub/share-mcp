package client

import (
	"bytes"
	"context"
	"crypto/rand"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"share/internal/protocol"
	"share/internal/tlsid"
)

func loopbackCandidates(port int) []string { return []string{fmt.Sprintf("127.0.0.1:%d", port)} }

func writeFile(t *testing.T, size int) (string, []byte) {
	t.Helper()
	data := make([]byte, size)
	rand.Read(data)
	p := filepath.Join(t.TempDir(), "payload.bin")
	if err := os.WriteFile(p, data, 0644); err != nil {
		t.Fatal(err)
	}
	return p, data
}

type senderFixture struct {
	e      *env
	sender *Sender
	recv   *API
	rcfg   Config
	ctx    context.Context
}

func newSenderFixture(t *testing.T, cands func(int) []string) *senderFixture {
	t.Helper()
	e := newEnv(t)
	sc := e.pair(t, "Desk", protocol.DeviceWindows)
	rc := e.pair(t, "Pixel", protocol.DeviceAndroid)
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(cancel)
	s := NewSender(NewAPI(sc), SenderOptions{Port: -1, Candidates: cands})
	go s.Run(ctx)
	if err := s.WaitReady(ctx, 3*time.Second); err != nil {
		t.Fatal(err)
	}
	return &senderFixture{e: e, sender: s, recv: NewAPI(rc), rcfg: rc, ctx: ctx}
}

func pinnedClient(fp string) *http.Client {
	return &http.Client{Timeout: 5 * time.Second, Transport: &http.Transport{TLSClientConfig: tlsid.PinnedClientConfig(fp)}}
}

func get(t *testing.T, c *http.Client, url, token, rng string) (int, []byte, error) {
	req, _ := http.NewRequest("GET", url, nil)
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	if rng != "" {
		req.Header.Set("Range", rng)
	}
	resp, err := c.Do(req)
	if err != nil {
		return 0, nil, err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	return resp.StatusCode, b, nil
}

func TestLocalEndpointRequiresToken(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	p, _ := writeFile(t, 1000)
	tr, err := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	if err != nil {
		t.Fatal(err)
	}
	ar, err := f.recv.Accept(f.ctx, tr.ID)
	if err != nil {
		t.Fatal(err)
	}
	if len(ar.LANCandidates) != 1 || !strings.HasPrefix(ar.LANCandidates[0], "127.0.0.1:") {
		t.Fatal(ar.LANCandidates)
	}
	c := pinnedClient(ar.CertFingerprint)
	base := "https://" + ar.LANCandidates[0] + "/t/" + tr.ID
	for _, tok := range []string{"", "x.00", f.rcfg.DeviceID + ".deadbeef"} {
		if st, _, err := get(t, c, base+"/ping", tok, ""); err != nil || st != 401 {
			t.Fatalf("token %q: %d %v", tok, st, err)
		}
	}
	if st, _, err := get(t, c, base+"/ping", ar.AccessToken, ""); err != nil || st != 204 {
		t.Fatal(st, err)
	}
	if st, _, _ := get(t, c, "https://"+ar.LANCandidates[0]+"/t/unknown/ping", ar.AccessToken, ""); st != 404 {
		t.Fatal(st)
	}
}

func TestLocalEndpointRange(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	p, data := writeFile(t, 100000)
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	if tr.Files[0].Name != "payload.bin" || tr.Files[0].Size != 100000 {
		t.Fatal(tr.Files)
	}
	ar, _ := f.recv.Accept(f.ctx, tr.ID)
	c := pinnedClient(ar.CertFingerprint)
	url := "https://" + ar.LANCandidates[0] + "/t/" + tr.ID + "/files/0"
	st, b, err := get(t, c, url, ar.AccessToken, "bytes=5-")
	if err != nil || st != 206 || !bytes.Equal(b, data[5:]) {
		t.Fatal(st, len(b), err)
	}
	st, b, _ = get(t, c, url, ar.AccessToken, "")
	if st != 200 || !bytes.Equal(b, data) {
		t.Fatal(st, len(b))
	}
	if st, _, _ := get(t, c, "https://"+ar.LANCandidates[0]+"/t/"+tr.ID+"/files/1", ar.AccessToken, ""); st != 404 {
		t.Fatal(st)
	}
}

func TestLocalEndpointModifiedFile(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	p, _ := writeFile(t, 1000)
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	ar, _ := f.recv.Accept(f.ctx, tr.ID)
	os.WriteFile(p, []byte("changed"), 0644)
	c := pinnedClient(ar.CertFingerprint)
	if st, _, _ := get(t, c, "https://"+ar.LANCandidates[0]+"/t/"+tr.ID+"/files/0", ar.AccessToken, ""); st != 409 {
		t.Fatal(st)
	}
}

func TestEndpointStopsWhenClosed(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	p, _ := writeFile(t, 1000)
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	ar, _ := f.recv.Accept(f.ctx, tr.ID)
	if err := f.sender.Cancel(f.ctx, tr.ID); err != nil {
		t.Fatal(err)
	}
	c := pinnedClient(ar.CertFingerprint)
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		st, _, err := get(t, c, "https://"+ar.LANCandidates[0]+"/t/"+tr.ID+"/ping", ar.AccessToken, "")
		if err != nil || st == 404 {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatal("endpoint still serving closed offer")
}

func TestRelayResponder(t *testing.T) {
	f := newSenderFixture(t, func(int) []string { return nil })
	p, data := writeFile(t, 300000)
	tr, _ := f.sender.OfferFiles(f.ctx, []string{p}, []string{"Pixel"}, false, "", 0)
	if _, err := f.recv.Accept(f.ctx, tr.ID); err != nil {
		t.Fatal(err)
	}
	rc, err := f.recv.RelayDownload(f.ctx, tr.ID, 0, 1234)
	if err != nil {
		t.Fatal(err)
	}
	b, _ := io.ReadAll(rc)
	rc.Close()
	if !bytes.Equal(b, data[1234:]) {
		t.Fatal(len(b))
	}
}

func TestSenderUpdates(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	tr, err := f.sender.OfferText(f.ctx, protocol.KindLink, "https://example.org", []string{"Pixel"}, false)
	if err != nil {
		t.Fatal(err)
	}
	f.recv.Accept(f.ctx, tr.ID)
	timeout := time.After(3 * time.Second)
	gotDelivered, gotClosed := false, false
	for !(gotDelivered && gotClosed) {
		select {
		case u := <-f.sender.Updates():
			if u.TransferID != tr.ID {
				continue
			}
			if u.Closed {
				gotClosed = true
			} else if u.Delivery.Status == protocol.StatusDelivered {
				gotDelivered = true
			}
		case <-timeout:
			t.Fatal("updates missing", gotDelivered, gotClosed)
		}
	}
}

func TestOfferFilesMissingFile(t *testing.T) {
	f := newSenderFixture(t, loopbackCandidates)
	_, err := f.sender.OfferFiles(f.ctx, []string{filepath.Join(t.TempDir(), "nope.apk")}, []string{"Pixel"}, false, "", 0)
	if err == nil || !strings.Contains(err.Error(), "nope.apk") {
		t.Fatal(err)
	}
}
