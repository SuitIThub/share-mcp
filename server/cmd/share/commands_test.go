package main

import (
	"bytes"
	"context"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"share/internal/client"
	"share/internal/protocol"
	"share/internal/server"
	"share/internal/store"
)

type cliEnv struct {
	ts      *httptest.Server
	st      *store.Store
	cfgPath string
}

func newCLIEnv(t *testing.T) *cliEnv {
	st, _ := store.Open(":memory:")
	srv := server.New(st, server.Config{})
	ts := httptest.NewServer(srv.Handler())
	t.Cleanup(func() { ts.Close(); srv.Close(); st.Close() })
	e := &cliEnv{ts: ts, st: st, cfgPath: filepath.Join(t.TempDir(), "config.json")}
	code, _ := st.CreatePairCode(time.Now())
	var out, errb bytes.Buffer
	if rc := run(context.Background(), []string{"-config", e.cfgPath, "pair", "-name", "Agent", ts.URL, code}, &out, &errb); rc != 0 {
		t.Fatal("pair failed", errb.String())
	}
	return e
}

func (e *cliEnv) device(t *testing.T, name string) *client.API {
	code, _ := e.st.CreatePairCode(time.Now())
	cfg, err := client.Pair(context.Background(), e.ts.URL, code, name, protocol.DeviceAndroid)
	if err != nil {
		t.Fatal(err)
	}
	return client.NewAPI(cfg)
}

func TestSendRequiresTarget(t *testing.T) {
	e := newCLIEnv(t)
	var out, errb bytes.Buffer
	rc := run(context.Background(), []string{"-config", e.cfgPath, "send", "x.apk"}, &out, &errb)
	if rc != 2 || !strings.Contains(errb.String(), "--to") {
		t.Fatal(rc, errb.String())
	}
}

func TestUnknownTargetMessage(t *testing.T) {
	e := newCLIEnv(t)
	p := filepath.Join(t.TempDir(), "a.txt")
	os.WriteFile(p, []byte("x"), 0644)
	var out, errb bytes.Buffer
	rc := run(context.Background(), []string{"-config", e.cfgPath, "send", p, "--to", "foo"}, &out, &errb)
	if rc != 2 || !strings.Contains(errb.String(), "foo") {
		t.Fatal(rc, errb.String())
	}
}

func TestSendEndToEnd(t *testing.T) {
	e := newCLIEnv(t)
	pixel := e.device(t, "Pixel")
	p := filepath.Join(t.TempDir(), "app.apk")
	os.WriteFile(p, bytes.Repeat([]byte("a"), 100000), 0644)
	dl := t.TempDir()
	go func() { // receiver: accept the first incoming offer
		for i := 0; i < 100; i++ {
			in, _ := pixel.Incoming(context.Background())
			if len(in) > 0 {
				client.NewReceiver(pixel, dl).Accept(context.Background(), in[0], nil)
				return
			}
			time.Sleep(50 * time.Millisecond)
		}
	}()
	var out, errb bytes.Buffer
	rc := run(context.Background(), []string{"-config", e.cfgPath, "send", "--to", "pix", p}, &out, &errb)
	if rc != 0 || !strings.Contains(out.String(), "delivered") {
		t.Fatal(rc, out.String(), errb.String())
	}
	if _, err := os.Stat(filepath.Join(dl, "app.apk")); err != nil {
		t.Fatal(err)
	}
}

func TestTextAndDevices(t *testing.T) {
	e := newCLIEnv(t)
	pixel := e.device(t, "Pixel")
	go func() {
		for i := 0; i < 100; i++ {
			in, _ := pixel.Incoming(context.Background())
			if len(in) > 0 {
				pixel.Accept(context.Background(), in[0].ID)
				return
			}
			time.Sleep(50 * time.Millisecond)
		}
	}()
	var out, errb bytes.Buffer
	// text/link need no data path: the command returns right after offering, even for --all
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	start := time.Now()
	if rc := run(ctx, []string{"-config", e.cfgPath, "link", "https://example.org", "--all"}, &out, &errb); rc != 0 || time.Since(start) > 4*time.Second {
		t.Fatal(rc, time.Since(start), errb.String())
	}
	out.Reset()
	if rc := run(context.Background(), []string{"-config", e.cfgPath, "devices"}, &out, &errb); rc != 0 || !strings.Contains(out.String(), "Pixel") || strings.Contains(out.String(), "Agent") {
		t.Fatal(rc, out.String())
	}
}

func TestNotPaired(t *testing.T) {
	var out, errb bytes.Buffer
	rc := run(context.Background(), []string{"-config", filepath.Join(t.TempDir(), "none.json"), "devices"}, &out, &errb)
	if rc != 1 || !strings.Contains(errb.String(), "share pair") {
		t.Fatal(rc, errb.String())
	}
}
