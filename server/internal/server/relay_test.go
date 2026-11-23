package server

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"testing"
	"time"

	"share/internal/protocol"
)

// fakeRelaySender answers relay.request events by uploading data[offset:].
func fakeRelaySender(t *testing.T, e *testEnv, tok string, data []byte, uploadTok string) {
	ch, _ := e.ws(t, tok)
	go func() {
		for ev := range ch {
			if ev.Type != protocol.EvRelayRequest {
				continue
			}
			var rr protocol.RelayRequest
			json.Unmarshal(ev.Data, &rr)
			req, _ := http.NewRequest("POST", e.ts.URL+"/v1/relay/"+rr.RelayID, bytes.NewReader(data[rr.Offset:]))
			req.Header.Set("Authorization", "Bearer "+uploadTok)
			if resp, err := http.DefaultClient.Do(req); err == nil {
				resp.Body.Close()
			}
		}
	}()
}

func relaySetup(t *testing.T, mod ...func(*Config)) (e *testEnv, stok, rtok, id string, data []byte) {
	e = newTestServer(t, mod...)
	sid, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	_, rtok = e.pair(t, "Pixel", protocol.DeviceAndroid)
	data = bytes.Repeat([]byte("0123456789"), 100000) // 1 MB
	req := protocol.CreateTransferRequest{Kind: protocol.KindFiles, Targets: []string{"Pixel"},
		Files: []protocol.FileItem{{Name: "f.bin", Size: int64(len(data)), MIME: "application/octet-stream"}}}
	resp := e.create(t, stok, req)
	_ = sid
	return e, stok, rtok, resp.Transfer.ID, data
}

func relayGet(t *testing.T, e *testEnv, tok, id, rng string) (*http.Response, []byte) {
	t.Helper()
	req, _ := http.NewRequest("GET", e.ts.URL+"/v1/transfers/"+id+"/files/0", nil)
	req.Header.Set("Authorization", "Bearer "+tok)
	if rng != "" {
		req.Header.Set("Range", rng)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	b, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	return resp, b
}

func TestRelayStreamsRange(t *testing.T) {
	e, stok, rtok, id, data := relaySetup(t)
	fakeRelaySender(t, e, stok, data, stok)
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, nil)
	resp, b := relayGet(t, e, rtok, id, "bytes=1000-")
	if resp.StatusCode != 206 || !bytes.Equal(b, data[1000:]) {
		t.Fatal(resp.StatusCode, len(b))
	}
	if cr := resp.Header.Get("Content-Range"); cr != "bytes 1000-999999/1000000" {
		t.Fatal(cr)
	}
	resp, b = relayGet(t, e, rtok, id, "")
	if resp.StatusCode != 200 || !bytes.Equal(b, data) || resp.Header.Get("Content-Type") != "application/octet-stream" {
		t.Fatal(resp.StatusCode, len(b))
	}
}

func TestRelayRequiresAccept(t *testing.T) {
	e, stok, rtok, id, data := relaySetup(t)
	fakeRelaySender(t, e, stok, data, stok)
	if resp, _ := relayGet(t, e, rtok, id, ""); resp.StatusCode != 403 {
		t.Fatal(resp.StatusCode)
	}
}

// Review Focus 2: sender absent -> 502 quickly instead of hanging.
func TestRelaySenderAbsent(t *testing.T) {
	e, stok, rtok, id, _ := relaySetup(t, func(c *Config) { c.RelayWait = 200 * time.Millisecond })
	e.ws(t, stok) // online but never uploads
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, nil)
	start := time.Now()
	resp, _ := relayGet(t, e, rtok, id, "")
	if resp.StatusCode != 502 || time.Since(start) > time.Second {
		t.Fatal(resp.StatusCode, time.Since(start))
	}
}

func TestRelaySenderOffline(t *testing.T) {
	e, _, rtok, id, _ := relaySetup(t)
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, nil)
	start := time.Now()
	resp, _ := relayGet(t, e, rtok, id, "")
	if resp.StatusCode != 502 || time.Since(start) > time.Second {
		t.Fatal(resp.StatusCode)
	}
}

func TestRelayWrongUploader(t *testing.T) {
	e, stok, rtok, id, data := relaySetup(t, func(c *Config) { c.RelayWait = 500 * time.Millisecond })
	_, otok := e.pair(t, "Other", protocol.DeviceWindows)
	got := make(chan int, 1)
	ch, _ := e.ws(t, stok)
	go func() {
		for ev := range ch {
			if ev.Type == protocol.EvRelayRequest {
				var rr protocol.RelayRequest
				json.Unmarshal(ev.Data, &rr)
				req, _ := http.NewRequest("POST", e.ts.URL+"/v1/relay/"+rr.RelayID, bytes.NewReader(data))
				req.Header.Set("Authorization", "Bearer "+otok)
				resp, err := http.DefaultClient.Do(req)
				if err == nil {
					got <- resp.StatusCode
					resp.Body.Close()
				}
			}
		}
	}()
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, nil)
	relayGet(t, e, rtok, id, "")
	select {
	case st := <-got:
		if st != 403 {
			t.Fatal(st)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("no upload attempt")
	}
	if st := e.do(t, stok, "POST", "/v1/relay/unknown", nil, nil); st != 410 {
		t.Fatal(st)
	}
}

func TestRelayRangeUnsupported(t *testing.T) {
	e, stok, rtok, id, data := relaySetup(t)
	fakeRelaySender(t, e, stok, data, stok)
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, nil)
	if resp, _ := relayGet(t, e, rtok, id, "bytes=0-10"); resp.StatusCode != 416 {
		t.Fatal(resp.StatusCode)
	}
	if resp, _ := relayGet(t, e, rtok, id, "bytes=2000000-"); resp.StatusCode != 416 {
		t.Fatal(resp.StatusCode)
	}
}

func TestRelayAbortedOnCancel(t *testing.T) {
	e, stok, rtok, id, _ := relaySetup(t, func(c *Config) { c.RelayWait = 5 * time.Second })
	e.ws(t, stok)
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, nil)
	done := make(chan int, 1)
	go func() {
		resp, _ := relayGet(t, e, rtok, id, "")
		done <- resp.StatusCode
	}()
	time.Sleep(100 * time.Millisecond)
	e.do(t, stok, "DELETE", "/v1/transfers/"+id, nil, nil)
	select {
	case st := <-done:
		if st != 502 && st != 410 {
			t.Fatal(st)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("relay not aborted on cancel")
	}
	_ = context.Background
}

func TestRelayAbortedMidStream(t *testing.T) {
	e, stok, rtok, id, _ := relaySetup(t)
	ch, _ := e.ws(t, stok)
	pr, pw := io.Pipe()
	go func() {
		for ev := range ch {
			if ev.Type == protocol.EvRelayRequest {
				var rr protocol.RelayRequest
				json.Unmarshal(ev.Data, &rr)
				req, _ := http.NewRequest("POST", e.ts.URL+"/v1/relay/"+rr.RelayID, pr)
				req.Header.Set("Authorization", "Bearer "+stok)
				if resp, err := http.DefaultClient.Do(req); err == nil {
					resp.Body.Close()
				}
			}
		}
	}()
	go func() { // trickle data, never finish
		for i := 0; i < 1000; i++ {
			if _, err := pw.Write(make([]byte, 1024)); err != nil {
				return
			}
			time.Sleep(5 * time.Millisecond)
		}
	}()
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, nil)
	done := make(chan struct{})
	go func() { relayGet(t, e, rtok, id, ""); close(done) }()
	time.Sleep(200 * time.Millisecond)
	e.do(t, stok, "DELETE", "/v1/transfers/"+id, nil, nil)
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("stream not aborted on cancel")
	}
	pw.Close()
}
