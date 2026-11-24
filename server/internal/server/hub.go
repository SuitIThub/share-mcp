package server

import (
	"context"
	"encoding/json"
	"net/http"
	"sync"
	"time"

	"github.com/coder/websocket"

	"share/internal/protocol"
)

type conn struct {
	out    chan []byte
	cancel context.CancelFunc
}

type hub struct {
	mu           sync.Mutex
	conns        map[string]map[*conn]struct{}
	onDisconnect []func(string)
	onConnect    []func(string)
}

func newHub() *hub { return &hub{conns: map[string]map[*conn]struct{}{}} }

func (h *hub) Online(id string) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	return len(h.conns[id]) > 0
}

// Send delivers ev to all connections of the device. Returns false if none.
func (h *hub) Send(id string, ev protocol.Event) bool {
	b, _ := json.Marshal(ev)
	h.mu.Lock()
	defer h.mu.Unlock()
	cs := h.conns[id]
	for c := range cs {
		select {
		case c.out <- b:
		default:
			c.cancel() // slow consumer: drop connection, client reconnects and resyncs
		}
	}
	return len(cs) > 0
}

// OnDisconnect registers fn, called when a device's last connection closes.
func (h *hub) OnDisconnect(fn func(string)) {
	h.mu.Lock()
	h.onDisconnect = append(h.onDisconnect, fn)
	h.mu.Unlock()
}

// OnConnect registers fn, called when a device gets its first connection.
func (h *hub) OnConnect(fn func(string)) {
	h.mu.Lock()
	h.onConnect = append(h.onConnect, fn)
	h.mu.Unlock()
}

func (h *hub) add(id string, c *conn) {
	h.mu.Lock()
	if h.conns[id] == nil {
		h.conns[id] = map[*conn]struct{}{}
	}
	h.conns[id][c] = struct{}{}
	first := len(h.conns[id]) == 1
	fns := append([]func(string){}, h.onConnect...)
	h.mu.Unlock()
	if first {
		for _, fn := range fns {
			fn(id)
		}
	}
}

func (h *hub) remove(id string, c *conn) {
	h.mu.Lock()
	delete(h.conns[id], c)
	last := len(h.conns[id]) == 0
	if last {
		delete(h.conns, id)
	}
	fns := append([]func(string){}, h.onDisconnect...)
	h.mu.Unlock()
	if last {
		for _, fn := range fns {
			fn(id)
		}
	}
}

func (h *hub) closeAll() {
	h.mu.Lock()
	defer h.mu.Unlock()
	for _, cs := range h.conns {
		for c := range cs {
			c.cancel()
		}
	}
}

func (s *Server) handleEvents(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	ws, err := websocket.Accept(w, r, &websocket.AcceptOptions{InsecureSkipVerify: true})
	if err != nil {
		return
	}
	ctx, cancel := context.WithCancel(s.ctx)
	defer cancel()
	c := &conn{out: make(chan []byte, 256), cancel: cancel}
	s.hub.add(d.ID, c)
	defer s.hub.remove(d.ID, c)
	// reader: detects close; client messages are ignored
	go func() {
		defer cancel()
		for {
			if _, _, err := ws.Read(ctx); err != nil {
				return
			}
		}
	}()
	ping := time.NewTicker(30 * time.Second)
	defer ping.Stop()
	for {
		select {
		case <-ctx.Done():
			ws.Close(websocket.StatusGoingAway, "")
			return
		case b := <-c.out:
			wctx, wc := context.WithTimeout(ctx, 10*time.Second)
			err := ws.Write(wctx, websocket.MessageText, b)
			wc()
			if err != nil {
				return
			}
		case <-ping.C:
			pctx, pc := context.WithTimeout(ctx, 10*time.Second)
			err := ws.Ping(pctx)
			pc()
			if err != nil {
				return
			}
		}
	}
}
