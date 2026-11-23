package server

import (
	"context"
	"encoding/json"
	"net/http"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/coder/websocket"

	"share/internal/protocol"
)

// wsClient connects to /v1/events and returns a channel of events.
func (e *testEnv) ws(t *testing.T, token string) (<-chan protocol.Event, func()) {
	t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	url := "ws" + strings.TrimPrefix(e.ts.URL, "http") + "/v1/events"
	c, _, err := websocket.Dial(ctx, url, &websocket.DialOptions{HTTPHeader: http.Header{"Authorization": {"Bearer " + token}}})
	if err != nil {
		cancel()
		t.Fatal(err)
	}
	c.SetReadLimit(1 << 20)
	ch := make(chan protocol.Event, 64)
	go func() {
		defer close(ch)
		for {
			_, b, err := c.Read(ctx)
			if err != nil {
				return
			}
			var ev protocol.Event
			json.Unmarshal(b, &ev)
			ch <- ev
		}
	}()
	closeFn := func() { c.Close(websocket.StatusNormalClosure, ""); cancel() }
	t.Cleanup(closeFn)
	return ch, closeFn
}

func waitEvent(t *testing.T, ch <-chan protocol.Event, typ string) protocol.Event {
	t.Helper()
	timeout := time.After(3 * time.Second)
	for {
		select {
		case ev, ok := <-ch:
			if !ok {
				t.Fatalf("event channel closed waiting for %s", typ)
			}
			if ev.Type == typ {
				return ev
			}
		case <-timeout:
			t.Fatalf("timeout waiting for %s", typ)
		}
	}
}

func TestEventDelivered(t *testing.T) {
	e := newTestServer(t)
	id, tok := e.pair(t, "Desk", protocol.DeviceWindows)
	ch, _ := e.ws(t, tok)
	eventually(t, 2*time.Second, func() bool { return e.srv.hub.Online(id) })
	if !e.srv.hub.Send(id, protocol.NewEvent("test.ping", map[string]int{"n": 1})) {
		t.Fatal("send reported offline")
	}
	ev := waitEvent(t, ch, "test.ping")
	if string(ev.Data) != `{"n":1}` {
		t.Fatal(string(ev.Data))
	}
}

func TestOnlineFlag(t *testing.T) {
	e := newTestServer(t)
	_, tok := e.pair(t, "Desk", protocol.DeviceWindows)
	online := func() bool {
		var devs []protocol.Device
		e.do(t, tok, "GET", "/v1/devices", nil, &devs)
		return devs[0].Online
	}
	if online() {
		t.Fatal("online before connect")
	}
	_, closeFn := e.ws(t, tok)
	eventually(t, 2*time.Second, online)
	closeFn()
	eventually(t, 2*time.Second, func() bool { return !online() })
}

func TestDisconnectCallback(t *testing.T) {
	e := newTestServer(t)
	id, tok := e.pair(t, "Desk", protocol.DeviceWindows)
	var got atomic.Value
	e.srv.hub.OnDisconnect(func(dev string) { got.Store(dev) })
	_, closeFn := e.ws(t, tok)
	eventually(t, 2*time.Second, func() bool { return e.srv.hub.Online(id) })
	closeFn()
	eventually(t, 2*time.Second, func() bool { v, _ := got.Load().(string); return v == id })
}

func TestEventsRequireAuth(t *testing.T) {
	e := newTestServer(t)
	url := "ws" + strings.TrimPrefix(e.ts.URL, "http") + "/v1/events"
	_, resp, err := websocket.Dial(context.Background(), url, nil)
	if err == nil || resp == nil || resp.StatusCode != 401 {
		t.Fatal("unauthenticated websocket allowed")
	}
}
