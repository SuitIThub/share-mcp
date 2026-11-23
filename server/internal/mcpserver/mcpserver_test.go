package mcpserver

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"share/internal/client"
	"share/internal/protocol"
	"share/internal/server"
	"share/internal/store"
)

type fixture struct {
	sess  *mcp.ClientSession
	pixel *client.API
	st    *store.Store
	ts    *httptest.Server
}

func setup(t *testing.T) *fixture {
	t.Helper()
	st, _ := store.Open(":memory:")
	srv := server.New(st, server.Config{})
	ts := httptest.NewServer(srv.Handler())
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(func() { cancel(); ts.Close(); srv.Close(); st.Close() })
	pairDev := func(name string, typ protocol.DeviceType) client.Config {
		code, _ := st.CreatePairCode(time.Now())
		cfg, err := client.Pair(ctx, ts.URL, code, name, typ)
		if err != nil {
			t.Fatal(err)
		}
		return cfg
	}
	agentAPI := client.NewAPI(pairDev("Agent", protocol.DeviceAgent))
	pixel := client.NewAPI(pairDev("Pixel", protocol.DeviceAndroid))
	pairDev("Other Agent", protocol.DeviceAgent)
	sender := client.NewSender(agentAPI, client.SenderOptions{Port: -1, Candidates: func(p int) []string { return []string{fmt.Sprintf("127.0.0.1:%d", p)} }})
	go sender.Run(ctx)
	s := New(agentAPI, sender)
	st1, st2 := mcp.NewInMemoryTransports()
	if _, err := s.Connect(ctx, st1, nil); err != nil {
		t.Fatal(err)
	}
	c := mcp.NewClient(&mcp.Implementation{Name: "test", Version: "1"}, nil)
	sess, err := c.Connect(ctx, st2, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { sess.Close() })
	return &fixture{sess: sess, pixel: pixel, st: st, ts: ts}
}

func (f *fixture) call(t *testing.T, name string, args map[string]any, out any) (*mcp.CallToolResult, string) {
	t.Helper()
	res, err := f.sess.CallTool(context.Background(), &mcp.CallToolParams{Name: name, Arguments: args})
	if err != nil {
		t.Fatal(err)
	}
	var text string
	for _, c := range res.Content {
		if tc, ok := c.(*mcp.TextContent); ok {
			text += tc.Text
		}
	}
	if out != nil && !res.IsError {
		b, _ := json.Marshal(res.StructuredContent)
		json.Unmarshal(b, out)
	}
	return res, text
}

func TestListDevicesExcludesAgents(t *testing.T) {
	f := setup(t)
	var out ListDevicesOutput
	res, _ := f.call(t, "list_devices", map[string]any{}, &out)
	if res.IsError || len(out.Devices) != 1 || out.Devices[0].Name != "Pixel" || out.Devices[0].Type != "android" {
		t.Fatal(out)
	}
}

func TestSendFilesNeedsExactlyOneTargetMode(t *testing.T) {
	f := setup(t)
	p := filepath.Join(t.TempDir(), "a.apk")
	os.WriteFile(p, []byte("x"), 0644)
	for _, args := range []map[string]any{
		{"paths": []string{p}},
		{"paths": []string{p}, "targets": []string{"Pixel"}, "all": true},
	} {
		if res, _ := f.call(t, "send_files", args, nil); !res.IsError {
			t.Fatal("accepted", args)
		}
	}
}

func TestMissingFile(t *testing.T) {
	f := setup(t)
	missing := filepath.Join(t.TempDir(), "gone.apk")
	res, text := f.call(t, "send_files", map[string]any{"paths": []string{missing}, "targets": []string{"Pixel"}}, nil)
	if !res.IsError || !strings.Contains(text, "gone.apk") {
		t.Fatal(text)
	}
}

func TestSendFilesAndStatus(t *testing.T) {
	f := setup(t)
	p := filepath.Join(t.TempDir(), "app.apk")
	os.WriteFile(p, make([]byte, 200000), 0644)
	var sent SendOutput
	res, text := f.call(t, "send_files", map[string]any{"paths": []string{p}, "targets": []string{"pix"}, "message": "build 42"}, &sent)
	if res.IsError || sent.TransferID == "" || len(sent.Recipients) != 1 || sent.Recipients[0] != "Pixel" {
		t.Fatal(text)
	}
	dl := t.TempDir()
	go func() {
		in, _ := f.pixel.Incoming(context.Background())
		for _, tr := range in {
			if tr.ID == sent.TransferID {
				client.NewReceiver(f.pixel, dl).Accept(context.Background(), tr, nil)
			}
		}
	}()
	deadline := time.Now().Add(15 * time.Second)
	for time.Now().Before(deadline) {
		var st StatusOutput
		res, text := f.call(t, "get_transfer_status", map[string]any{"transfer_id": sent.TransferID, "wait_seconds": 5}, &st)
		if res.IsError {
			t.Fatal(text)
		}
		if len(st.Deliveries) == 1 && st.Deliveries[0].Status == "delivered" {
			if st.Deliveries[0].Device != "Pixel" || st.Deliveries[0].Percent != 100 {
				t.Fatal(st)
			}
			if _, err := os.Stat(filepath.Join(dl, "app.apk")); err != nil {
				t.Fatal(err)
			}
			return
		}
	}
	t.Fatal("never delivered")
}

func TestSendTextAndCancel(t *testing.T) {
	f := setup(t)
	var sent SendOutput
	if res, text := f.call(t, "send_link", map[string]any{"url": "https://example.org", "all": true}, &sent); res.IsError {
		t.Fatal(text)
	}
	if res, text := f.call(t, "cancel_transfer", map[string]any{"transfer_id": sent.TransferID}, nil); res.IsError {
		t.Fatal(text)
	}
	var st StatusOutput
	f.call(t, "get_transfer_status", map[string]any{"transfer_id": sent.TransferID}, &st)
	if !st.Closed || st.Deliveries[0].Status != "cancelled" {
		t.Fatal(st)
	}
}
