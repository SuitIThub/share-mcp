package client

import (
	"context"
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"github.com/coder/websocket"

	"share/internal/protocol"
)

// EvConnected is emitted (client-side only) after the event stream (re)connects,
// so callers can resync state they may have missed.
const EvConnected = "client.connected"

// Events keeps a websocket to /v1/events open (reconnecting with backoff 1s..30s)
// and delivers events until ctx is done, then closes the channel.
func (a *API) Events(ctx context.Context) <-chan protocol.Event {
	ch := make(chan protocol.Event, 64)
	go func() {
		defer close(ch)
		backoff := time.Second
		for ctx.Err() == nil {
			if a.eventsOnce(ctx, ch) {
				backoff = time.Second
			}
			select {
			case <-ctx.Done():
				return
			case <-time.After(backoff):
			}
			if backoff < 30*time.Second {
				backoff *= 2
			}
		}
	}()
	return ch
}

func (a *API) eventsOnce(ctx context.Context, ch chan<- protocol.Event) (connected bool) {
	url := "ws" + strings.TrimPrefix(a.cfg.ServerURL, "http") + "/v1/events"
	c, _, err := websocket.Dial(ctx, url, &websocket.DialOptions{
		HTTPClient: a.http,
		HTTPHeader: http.Header{"Authorization": {"Bearer " + a.cfg.Token}},
	})
	if err != nil {
		return false
	}
	defer c.CloseNow()
	c.SetReadLimit(4 << 20)
	select {
	case ch <- protocol.Event{Type: EvConnected}:
	case <-ctx.Done():
		return true
	}
	for {
		_, b, err := c.Read(ctx)
		if err != nil {
			return true
		}
		var ev protocol.Event
		if json.Unmarshal(b, &ev) != nil {
			continue
		}
		select {
		case ch <- ev:
		case <-ctx.Done():
			return true
		}
	}
}
