// Package server implements the share broker: devices, offers, events and relay.
package server

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"time"

	"share/internal/protocol"
	"share/internal/store"
)

type Config struct {
	MaxOfferMinutes int
	Now             func() time.Time
	Notifier        Notifier
	RelayWait       time.Duration // how long a relay download waits for the sender; default 15s
	SenderGrace     time.Duration // how long a disconnected sender keeps its offers; default 30s
	StallTimeout    time.Duration // accepted/downloading without progress after expiry; default 2min
	PurgeAfter      time.Duration // metadata kept after close; default 1h
}

type Server struct {
	st     *store.Store
	cfg    Config
	mux    *http.ServeMux
	hub    *hub
	relays *relayBroker
	ctx    context.Context
	cancel context.CancelFunc
}

type ctxKey struct{}

func New(st *store.Store, cfg Config) *Server {
	if cfg.MaxOfferMinutes <= 0 || cfg.MaxOfferMinutes > 10 {
		cfg.MaxOfferMinutes = 10
	}
	if cfg.Now == nil {
		cfg.Now = time.Now
	}
	if cfg.Notifier == nil {
		cfg.Notifier = NopNotifier{}
	}
	if cfg.RelayWait == 0 {
		cfg.RelayWait = 15 * time.Second
	}
	if cfg.SenderGrace == 0 {
		cfg.SenderGrace = 30 * time.Second
	}
	if cfg.StallTimeout == 0 {
		cfg.StallTimeout = 2 * time.Minute
	}
	if cfg.PurgeAfter == 0 {
		cfg.PurgeAfter = time.Hour
	}
	ctx, cancel := context.WithCancel(context.Background())
	s := &Server{st: st, cfg: cfg, mux: http.NewServeMux(), ctx: ctx, cancel: cancel}
	s.hub = newHub()
	s.relays = newRelayBroker()
	s.routes()
	return s
}

// Close stops background work and closes event connections.
func (s *Server) Close() {
	s.cancel()
	s.hub.closeAll()
}

func (s *Server) Handler() http.Handler { return s.mux }

func (s *Server) routes() {
	s.mux.HandleFunc("POST /v1/pair", s.handlePair)
	s.mux.Handle("POST /v1/pair/codes", s.auth(s.handleCreateCode))
	s.mux.Handle("GET /v1/devices", s.auth(s.handleDevices))
	s.mux.Handle("PATCH /v1/devices/me", s.auth(s.handlePatchMe))
	s.mux.Handle("DELETE /v1/devices/me", s.auth(s.handleDeleteMe))
	s.mux.Handle("GET /v1/events", s.auth(s.handleEvents))
	s.transferRoutes()
}

func (s *Server) auth(h func(http.ResponseWriter, *http.Request, protocol.Device)) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		tok := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
		if tok == "" {
			tok = r.URL.Query().Get("token")
		}
		if tok == "" {
			httpError(w, 401, "missing token")
			return
		}
		d, err := s.st.DeviceByToken(tok)
		if err != nil {
			httpError(w, 401, "invalid token")
			return
		}
		s.st.TouchDevice(d.ID, s.cfg.Now())
		h(w, r, d)
	})
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(v)
}

func httpError(w http.ResponseWriter, status int, msg string) {
	writeJSON(w, status, map[string]string{"error": msg})
}

func readJSON(r *http.Request, v any) error {
	if r.Body == nil {
		return errors.New("empty body")
	}
	dec := json.NewDecoder(http.MaxBytesReader(nil, r.Body, 1<<20))
	return dec.Decode(v)
}
