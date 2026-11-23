package server

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"
	"time"

	"share/internal/protocol"
	"share/internal/store"
)

type fakeClock struct {
	mu sync.Mutex
	t  time.Time
}

func (c *fakeClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.t
}

func (c *fakeClock) Advance(d time.Duration) {
	c.mu.Lock()
	c.t = c.t.Add(d)
	c.mu.Unlock()
}

type testEnv struct {
	ts    *httptest.Server
	srv   *Server
	st    *store.Store
	clock *fakeClock
}

func newTestServer(t *testing.T, mod ...func(*Config)) *testEnv {
	t.Helper()
	st, err := store.Open(":memory:")
	if err != nil {
		t.Fatal(err)
	}
	clock := &fakeClock{t: time.Date(2026, 1, 1, 12, 0, 0, 0, time.UTC)}
	cfg := Config{MaxOfferMinutes: 10, Now: clock.Now}
	for _, m := range mod {
		m(&cfg)
	}
	srv := New(st, cfg)
	ts := httptest.NewServer(srv.Handler())
	t.Cleanup(func() {
		ts.Close()
		srv.Close()
		st.Close()
	})
	return &testEnv{ts: ts, srv: srv, st: st, clock: clock}
}

func (e *testEnv) pair(t *testing.T, name string, typ protocol.DeviceType) (string, string) {
	t.Helper()
	code, err := e.st.CreatePairCode(e.clock.Now())
	if err != nil {
		t.Fatal(err)
	}
	var resp protocol.PairResponse
	st := e.do(t, "", "POST", "/v1/pair", protocol.PairRequest{Code: code, Name: name, Type: typ, CertFingerprint: "fp-" + name}, &resp)
	if st != 200 {
		t.Fatalf("pair status %d", st)
	}
	return resp.DeviceID, resp.Token
}

// do performs a JSON request; out may be nil. Returns HTTP status.
func (e *testEnv) do(t *testing.T, token, method, path string, body any, out any) int {
	t.Helper()
	var rd io.Reader
	if body != nil {
		b, _ := json.Marshal(body)
		rd = bytes.NewReader(b)
	}
	req, _ := http.NewRequest(method, e.ts.URL+path, rd)
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if out != nil && resp.StatusCode < 300 {
		if err := json.NewDecoder(resp.Body).Decode(out); err != nil {
			t.Fatal(err)
		}
	}
	return resp.StatusCode
}

func eventually(t *testing.T, d time.Duration, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(d)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("condition not met in time")
}
