package server

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"

	"share/internal/protocol"
)

// upload is the sender's body handed to the waiting receiver handler.
type upload struct {
	body io.Reader
	done chan error // receiver handler reports copy result
}

type relay struct {
	transferID string
	senderID   string
	ch         chan upload
	ctx        context.Context
	cancel     context.CancelFunc
}

type relayBroker struct {
	mu     sync.Mutex
	relays map[string]*relay
}

func newRelayBroker() *relayBroker { return &relayBroker{relays: map[string]*relay{}} }

func (b *relayBroker) add(r *relay, id string) {
	b.mu.Lock()
	b.relays[id] = r
	b.mu.Unlock()
}

func (b *relayBroker) remove(id string) {
	b.mu.Lock()
	delete(b.relays, id)
	b.mu.Unlock()
}

func (b *relayBroker) get(id string) *relay {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.relays[id]
}

// abortTransfer cancels all relays of a transfer.
func (b *relayBroker) abortTransfer(transferID string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	for _, r := range b.relays {
		if r.transferID == transferID {
			r.cancel()
		}
	}
}

func (s *Server) relayRoutes() {
	s.mux.Handle("GET /v1/transfers/{id}/files/{n}", s.auth(s.handleRelayDownload))
	s.mux.Handle("POST /v1/relay/{rid}", s.auth(s.handleRelayUpload))
}

// parseRange supports only "" and "bytes=N-".
func parseRange(h string, size int64) (int64, bool) {
	if h == "" {
		return 0, true
	}
	if !strings.HasPrefix(h, "bytes=") || !strings.HasSuffix(h, "-") {
		return 0, false
	}
	n, err := strconv.ParseInt(strings.TrimSuffix(strings.TrimPrefix(h, "bytes="), "-"), 10, 64)
	if err != nil || n < 0 || n > size {
		return 0, false
	}
	return n, true
}

func (s *Server) handleRelayDownload(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	t, dl, ok := s.receiverDelivery(w, r, d)
	if !ok {
		return
	}
	if dl.Status != protocol.StatusAccepted && dl.Status != protocol.StatusDownloading {
		httpError(w, 403, "not accepted")
		return
	}
	if t.Closed {
		httpError(w, 410, "offer closed")
		return
	}
	n, err := strconv.Atoi(r.PathValue("n"))
	if err != nil || n < 0 || n >= len(t.Files) {
		httpError(w, 404, "no such file")
		return
	}
	f := t.Files[n]
	offset, ok := parseRange(r.Header.Get("Range"), f.Size)
	if !ok {
		w.Header().Set("Content-Range", fmt.Sprintf("bytes */%d", f.Size))
		httpError(w, 416, "only 'bytes=N-' ranges are supported")
		return
	}
	ctx, cancel := context.WithCancel(r.Context())
	defer cancel()
	rid := newID()
	rl := &relay{transferID: t.ID, senderID: t.SenderID, ch: make(chan upload), ctx: ctx, cancel: cancel}
	s.relays.add(rl, rid)
	defer s.relays.remove(rid)
	if !s.hub.Send(t.SenderID, protocol.NewEvent(protocol.EvRelayRequest, protocol.RelayRequest{RelayID: rid, TransferID: t.ID, FileIndex: n, Offset: offset})) {
		httpError(w, 502, "sender offline")
		return
	}
	timer := time.NewTimer(s.cfg.RelayWait)
	defer timer.Stop()
	var up upload
	select {
	case up = <-rl.ch:
	case <-timer.C:
		httpError(w, 502, "sender did not respond")
		return
	case <-ctx.Done():
		httpError(w, 502, "relay aborted")
		return
	}
	remaining := f.Size - offset
	mime := f.MIME
	if mime == "" {
		mime = "application/octet-stream"
	}
	w.Header().Set("Content-Type", mime)
	w.Header().Set("Content-Length", strconv.FormatInt(remaining, 10))
	w.Header().Set("Accept-Ranges", "bytes")
	if offset > 0 {
		w.Header().Set("Content-Range", fmt.Sprintf("bytes %d-%d/%d", offset, f.Size-1, f.Size))
		w.WriteHeader(206)
	} else {
		w.WriteHeader(200)
	}
	written, err := copyFlush(w, up.body, remaining)
	if err == nil && written < remaining {
		err = io.ErrUnexpectedEOF
	}
	up.done <- err
}

// copyFlush copies up to n bytes, flushing after each chunk. Nothing touches disk.
func copyFlush(w http.ResponseWriter, r io.Reader, n int64) (int64, error) {
	fl, _ := w.(http.Flusher)
	buf := make([]byte, 256*1024)
	var total int64
	lr := io.LimitReader(r, n)
	for {
		k, rerr := lr.Read(buf)
		if k > 0 {
			if _, werr := w.Write(buf[:k]); werr != nil {
				return total, werr
			}
			total += int64(k)
			if fl != nil {
				fl.Flush()
			}
		}
		if rerr == io.EOF {
			return total, nil
		}
		if rerr != nil {
			return total, rerr
		}
	}
}

func (s *Server) handleRelayUpload(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	rl := s.relays.get(r.PathValue("rid"))
	if rl == nil {
		httpError(w, 410, "unknown or expired relay")
		return
	}
	if rl.senderID != d.ID {
		httpError(w, 403, "not the sender")
		return
	}
	up := upload{body: r.Body, done: make(chan error, 1)}
	select {
	case rl.ch <- up:
	case <-rl.ctx.Done():
		httpError(w, 410, "relay gone")
		return
	case <-r.Context().Done():
		return
	}
	select {
	case err := <-up.done:
		if err != nil {
			if errors.Is(err, context.Canceled) {
				httpError(w, 410, "receiver gone")
				return
			}
			httpError(w, 502, err.Error())
			return
		}
		w.WriteHeader(200)
	case <-rl.ctx.Done():
		httpError(w, 410, "relay aborted")
	}
}
