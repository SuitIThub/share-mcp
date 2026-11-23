package server

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"net/http"
	"strings"
	"sync"
	"time"

	"share/internal/protocol"
)

// offline tracks since when a device has had no event connection.
type offlineTracker struct {
	mu    sync.Mutex
	since map[string]time.Time
}

func (s *Server) transferRoutes() {
	s.off = &offlineTracker{since: map[string]time.Time{}}
	s.hub.OnDisconnect(func(id string) {
		s.off.mu.Lock()
		s.off.since[id] = s.cfg.Now()
		s.off.mu.Unlock()
	})
	s.hub.OnConnect(func(id string) {
		s.off.mu.Lock()
		delete(s.off.since, id)
		s.off.mu.Unlock()
	})
	s.mux.Handle("POST /v1/transfers", s.auth(s.handleCreate))
	s.mux.Handle("GET /v1/transfers", s.auth(s.handleList))
	s.mux.Handle("GET /v1/transfers/{id}", s.auth(s.handleGet))
	s.mux.Handle("DELETE /v1/transfers/{id}", s.auth(s.handleCancel))
	s.mux.Handle("POST /v1/transfers/{id}/accept", s.auth(s.handleAccept))
	s.mux.Handle("POST /v1/transfers/{id}/decline", s.auth(s.handleDecline))
	s.mux.Handle("POST /v1/transfers/{id}/progress", s.auth(s.handleProgress))
	s.mux.Handle("POST /v1/transfers/{id}/delivered", s.auth(s.handleDelivered))
	s.mux.Handle("POST /v1/transfers/{id}/fail", s.auth(s.handleFail))
	s.relayRoutes()
}

func newID() string {
	b := make([]byte, 8)
	rand.Read(b)
	return hex.EncodeToString(b)
}

func (s *Server) handleCreate(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	var req protocol.CreateTransferRequest
	if err := readJSON(r, &req); err != nil {
		httpError(w, 400, "bad json")
		return
	}
	switch req.Kind {
	case protocol.KindText, protocol.KindLink:
		if len(req.Text) > protocol.MaxTextBytes {
			httpError(w, 413, "text too large")
			return
		}
		if req.Text == "" {
			httpError(w, 400, "empty text")
			return
		}
		req.Files = nil
	case protocol.KindFiles:
		if len(req.Files) == 0 {
			httpError(w, 400, "no files")
			return
		}
		for _, f := range req.Files {
			if f.Name == "" || f.Size < 0 {
				httpError(w, 400, "invalid file entry")
				return
			}
		}
		req.Text = ""
	default:
		httpError(w, 400, "invalid kind")
		return
	}
	devs, err := s.st.Devices()
	if err != nil {
		httpError(w, 500, err.Error())
		return
	}
	var recipients []string
	if req.Broadcast {
		for _, x := range devs {
			if x.ID != d.ID && x.Type != protocol.DeviceAgent {
				recipients = append(recipients, x.ID)
			}
		}
	} else {
		seen := map[string]bool{}
		for _, tg := range req.Targets {
			var found *protocol.Device
			for i := range devs {
				if devs[i].ID == tg || strings.EqualFold(devs[i].Name, strings.TrimSpace(tg)) {
					found = &devs[i]
					break
				}
			}
			if found == nil || found.ID == d.ID || found.Type == protocol.DeviceAgent {
				httpError(w, 400, "invalid target: "+tg)
				return
			}
			if !seen[found.ID] {
				seen[found.ID] = true
				recipients = append(recipients, found.ID)
			}
		}
		if len(recipients) == 0 {
			httpError(w, 400, "no targets")
			return
		}
	}
	min := req.Minutes
	if min <= 0 || min > s.cfg.MaxOfferMinutes {
		min = s.cfg.MaxOfferMinutes
	}
	now := s.cfg.Now()
	t := protocol.Transfer{
		ID: newID(), SenderID: d.ID, Kind: req.Kind, Text: req.Text, Message: req.Message, Files: req.Files,
		Broadcast: req.Broadcast, Targets: recipients, CreatedAt: now, ExpiresAt: now.Add(time.Duration(min) * time.Minute),
	}
	if req.Broadcast {
		t.Targets = nil
	}
	secret := make([]byte, 32)
	rand.Read(secret)
	s.mu.Lock()
	err = s.st.CreateTransfer(t, secret, req.LANCandidates, recipients)
	if err == nil && req.Kind == protocol.KindFiles && !s.hub.Online(d.ID) {
		s.off.mu.Lock()
		if _, ok := s.off.since[d.ID]; !ok {
			s.off.since[d.ID] = now
		}
		s.off.mu.Unlock()
	}
	s.mu.Unlock()
	if err != nil {
		httpError(w, 500, err.Error())
		return
	}
	full, _ := s.st.Transfer(t.ID)
	offer := full
	offer.Deliveries = nil
	for _, rcp := range recipients {
		s.notifyReceiver(rcp, protocol.EvOfferNew, offer, t.ID)
	}
	writeJSON(w, 201, protocol.CreateTransferResponse{Transfer: full, Secret: hex.EncodeToString(secret)})
}

// notifyReceiver sends an event over the hub and, for Android devices with a push token, via the notifier.
func (s *Server) notifyReceiver(devID, typ string, data any, transferID string) {
	s.hub.Send(devID, protocol.NewEvent(typ, data))
	dev, err := s.st.Device(devID)
	if err != nil || dev.Type != protocol.DeviceAndroid {
		return
	}
	tok, _ := s.st.FCMToken(devID)
	if tok == "" {
		return
	}
	go func() {
		ctx, cancel := context.WithTimeout(s.ctx, 15*time.Second)
		defer cancel()
		if err := s.cfg.Notifier.Notify(ctx, tok, map[string]string{"type": typ, "transfer_id": transferID}); errors.Is(err, ErrTokenGone) {
			empty := ""
			s.st.UpdateDevice(devID, nil, &empty)
		}
	}()
}

func (s *Server) handleList(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	var out []protocol.Transfer
	var err error
	switch r.URL.Query().Get("role") {
	case "incoming":
		out, err = s.st.Incoming(d.ID)
	case "outgoing":
		out, err = s.st.Outgoing(d.ID)
	default:
		httpError(w, 400, "role must be incoming or outgoing")
		return
	}
	if err != nil {
		httpError(w, 500, err.Error())
		return
	}
	if out == nil {
		out = []protocol.Transfer{}
	}
	writeJSON(w, 200, out)
}

// loadFor returns the transfer as visible to device d; ok=false means not visible (404 written).
func (s *Server) loadFor(w http.ResponseWriter, id string, d protocol.Device) (protocol.Transfer, bool) {
	t, err := s.st.Transfer(id)
	if err != nil {
		httpError(w, 404, "not found")
		return t, false
	}
	if t.SenderID == d.ID {
		return t, true
	}
	for _, dl := range t.Deliveries {
		if dl.DeviceID == d.ID {
			t.Deliveries = []protocol.Delivery{dl}
			return t, true
		}
	}
	httpError(w, 404, "not found")
	return t, false
}

func (s *Server) handleGet(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	if t, ok := s.loadFor(w, r.PathValue("id"), d); ok {
		writeJSON(w, 200, t)
	}
}

func (s *Server) handleCancel(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	t, err := s.st.Transfer(r.PathValue("id"))
	if err != nil || t.SenderID != d.ID {
		httpError(w, 404, "not found")
		return
	}
	s.mu.Lock()
	s.cancelTransfer(t)
	s.mu.Unlock()
	w.WriteHeader(204)
}

// cancelTransfer marks open deliveries cancelled and closes the transfer. Caller holds s.mu.
func (s *Server) cancelTransfer(t protocol.Transfer) {
	if t.Closed {
		return
	}
	now := s.cfg.Now()
	for _, dl := range t.Deliveries {
		if dl.Status.Final() {
			continue
		}
		s.st.SetDelivery(t.ID, dl.DeviceID, protocol.StatusCancelled, dl.Path, dl.Bytes, now)
		s.notifyReceiver(dl.DeviceID, protocol.EvOfferRemoved, protocol.TransferRef{TransferID: t.ID}, t.ID)
	}
	s.closeTransfer(t.ID, t.SenderID)
}

func (s *Server) closeTransfer(id, senderID string) {
	s.st.CloseTransfer(id, s.cfg.Now())
	s.relays.abortTransfer(id)
	s.hub.Send(senderID, protocol.NewEvent(protocol.EvTransferClosed, protocol.TransferRef{TransferID: id}))
}

func (s *Server) cancelSenderTransfers(senderID string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	out, _ := s.st.Outgoing(senderID)
	for _, t := range out {
		s.cancelTransfer(t)
	}
}

// setStatus updates a delivery, informs the sender and closes the transfer when done. Caller holds s.mu.
func (s *Server) setStatus(t protocol.Transfer, dl protocol.Delivery) {
	now := s.cfg.Now()
	dl.UpdatedAt = now
	s.st.SetDelivery(t.ID, dl.DeviceID, dl.Status, dl.Path, dl.Bytes, now)
	s.hub.Send(t.SenderID, protocol.NewEvent(protocol.EvDeliveryUpdated, protocol.DeliveryUpdate{TransferID: t.ID, Delivery: dl}))
	s.maybeClose(t.ID)
}

func (s *Server) maybeClose(id string) {
	t, err := s.st.Transfer(id)
	if err != nil || t.Closed {
		return
	}
	for _, dl := range t.Deliveries {
		if !dl.Status.Final() {
			return
		}
	}
	if t.Broadcast && s.cfg.Now().Before(t.ExpiresAt) {
		return
	}
	s.closeTransfer(t.ID, t.SenderID)
}

// receiverDelivery loads transfer and the caller's delivery; writes 404 if not a receiver.
func (s *Server) receiverDelivery(w http.ResponseWriter, r *http.Request, d protocol.Device) (protocol.Transfer, protocol.Delivery, bool) {
	t, err := s.st.Transfer(r.PathValue("id"))
	if err != nil {
		httpError(w, 404, "not found")
		return t, protocol.Delivery{}, false
	}
	for _, dl := range t.Deliveries {
		if dl.DeviceID == d.ID {
			return t, dl, true
		}
	}
	httpError(w, 404, "not found")
	return t, protocol.Delivery{}, false
}

func (s *Server) handleAccept(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	s.mu.Lock()
	defer s.mu.Unlock()
	t, dl, ok := s.receiverDelivery(w, r, d)
	if !ok {
		return
	}
	switch dl.Status {
	case protocol.StatusAccepted, protocol.StatusDownloading:
		if t.Closed {
			httpError(w, 409, "offer closed")
			return
		}
	case protocol.StatusWaiting:
		if t.Closed || !s.cfg.Now().Before(t.ExpiresAt) {
			httpError(w, 409, "offer expired")
			return
		}
		dl.Status = protocol.StatusAccepted
		if t.Kind != protocol.KindFiles {
			dl.Status = protocol.StatusDelivered
		}
		s.setStatus(t, dl)
	case protocol.StatusDelivered:
		if t.Kind != protocol.KindFiles {
			writeJSON(w, 200, protocol.AcceptResponse{})
			return
		}
		httpError(w, 409, "already delivered")
		return
	default:
		httpError(w, 409, "offer no longer available")
		return
	}
	if t.Kind != protocol.KindFiles {
		writeJSON(w, 200, protocol.AcceptResponse{})
		return
	}
	secret, _ := s.st.TransferSecret(t.ID)
	lan, _ := s.st.LANCandidates(t.ID)
	if lan == nil {
		lan = []string{}
	}
	sender, _ := s.st.Device(t.SenderID)
	writeJSON(w, 200, protocol.AcceptResponse{
		LANCandidates:   lan,
		CertFingerprint: sender.CertFingerprint,
		AccessToken:     protocol.AccessToken(secret, t.ID, d.ID),
		RelayBase:       "/v1/transfers/" + t.ID + "/files/",
	})
}

func (s *Server) handleDecline(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	s.mu.Lock()
	defer s.mu.Unlock()
	t, dl, ok := s.receiverDelivery(w, r, d)
	if !ok {
		return
	}
	if dl.Status != protocol.StatusWaiting {
		httpError(w, 409, "cannot decline in state "+string(dl.Status))
		return
	}
	dl.Status = protocol.StatusDeclined
	s.setStatus(t, dl)
	w.WriteHeader(204)
}

func (s *Server) handleProgress(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	var req protocol.ProgressRequest
	if err := readJSON(r, &req); err != nil {
		httpError(w, 400, "bad json")
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	t, dl, ok := s.receiverDelivery(w, r, d)
	if !ok {
		return
	}
	if dl.Status != protocol.StatusAccepted && dl.Status != protocol.StatusDownloading {
		httpError(w, 409, "not accepted")
		return
	}
	dl.Status, dl.Bytes, dl.Path = protocol.StatusDownloading, req.Bytes, req.Path
	s.setStatus(t, dl)
	w.WriteHeader(204)
}

func (s *Server) finish(w http.ResponseWriter, r *http.Request, d protocol.Device, st protocol.Status) {
	s.mu.Lock()
	defer s.mu.Unlock()
	t, dl, ok := s.receiverDelivery(w, r, d)
	if !ok {
		return
	}
	if dl.Status != protocol.StatusAccepted && dl.Status != protocol.StatusDownloading {
		httpError(w, 409, "not accepted")
		return
	}
	dl.Status = st
	if st == protocol.StatusDelivered {
		dl.Bytes = t.TotalSize()
	}
	s.setStatus(t, dl)
	w.WriteHeader(204)
}

func (s *Server) handleDelivered(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	s.finish(w, r, d, protocol.StatusDelivered)
}

func (s *Server) handleFail(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	s.finish(w, r, d, protocol.StatusFailed)
}

// Run executes the background loop until ctx or the server is done.
func (s *Server) Run(ctx context.Context) {
	tk := time.NewTicker(5 * time.Second)
	defer tk.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-s.ctx.Done():
			return
		case <-tk.C:
			s.tick(ctx)
		}
	}
}

// tick performs one pass of expiry, stall detection, sender-gone cancellation and purge.
func (s *Server) tick(ctx context.Context) {
	s.mu.Lock()
	defer s.mu.Unlock()
	now := s.cfg.Now()
	open, err := s.st.OpenTransfers()
	if err != nil {
		return
	}
	for _, t := range open {
		if t.Kind == protocol.KindFiles && s.senderGone(t.SenderID, now) {
			s.cancelTransfer(t)
			continue
		}
		expired := !now.Before(t.ExpiresAt)
		for _, dl := range t.Deliveries {
			switch {
			case dl.Status == protocol.StatusWaiting && expired:
				dl.Status = protocol.StatusMissed
				s.st.SetDelivery(t.ID, dl.DeviceID, dl.Status, dl.Path, dl.Bytes, now)
				s.hub.Send(t.SenderID, protocol.NewEvent(protocol.EvDeliveryUpdated, protocol.DeliveryUpdate{TransferID: t.ID, Delivery: dl}))
				s.notifyReceiver(dl.DeviceID, protocol.EvOfferRemoved, protocol.TransferRef{TransferID: t.ID}, t.ID)
			case (dl.Status == protocol.StatusAccepted || dl.Status == protocol.StatusDownloading) && expired && now.Sub(dl.UpdatedAt) > s.cfg.StallTimeout:
				dl.Status = protocol.StatusFailed
				s.st.SetDelivery(t.ID, dl.DeviceID, dl.Status, dl.Path, dl.Bytes, now)
				s.hub.Send(t.SenderID, protocol.NewEvent(protocol.EvDeliveryUpdated, protocol.DeliveryUpdate{TransferID: t.ID, Delivery: dl}))
			}
		}
		s.maybeClose(t.ID)
	}
	s.st.PurgeClosedBefore(now.Add(-s.cfg.PurgeAfter))
}

func (s *Server) senderGone(id string, now time.Time) bool {
	s.off.mu.Lock()
	defer s.off.mu.Unlock()
	since, ok := s.off.since[id]
	return ok && now.Sub(since) > s.cfg.SenderGrace
}

// Tick runs one background pass immediately (used by tests and tools).
func (s *Server) Tick(ctx context.Context) { s.tick(ctx) }
