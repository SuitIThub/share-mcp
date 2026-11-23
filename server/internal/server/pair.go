package server

import (
	"errors"
	"net/http"
	"strings"

	"share/internal/protocol"
	"share/internal/store"
)

func (s *Server) handlePair(w http.ResponseWriter, r *http.Request) {
	var req protocol.PairRequest
	if err := readJSON(r, &req); err != nil {
		httpError(w, 400, "bad json")
		return
	}
	if !req.Type.Valid() || strings.TrimSpace(req.Name) == "" || len(req.Name) > 64 {
		httpError(w, 400, "invalid name or type")
		return
	}
	id, tok, err := s.st.RedeemPairCode(req.Code, s.cfg.Now(), req.Name, req.Type, req.CertFingerprint)
	switch {
	case errors.Is(err, store.ErrInvalidCode):
		httpError(w, 403, "invalid or expired code")
	case errors.Is(err, store.ErrConflict):
		httpError(w, 409, "name already in use")
	case err != nil:
		httpError(w, 500, err.Error())
	default:
		writeJSON(w, 200, protocol.PairResponse{DeviceID: id, Token: tok})
	}
}

func (s *Server) handleCreateCode(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	now := s.cfg.Now()
	code, err := s.st.CreatePairCode(now)
	if err != nil {
		httpError(w, 500, err.Error())
		return
	}
	writeJSON(w, 200, protocol.PairCodeResponse{Code: code, ExpiresAt: now.Add(store.PairCodeTTL)})
}

func (s *Server) handleDevices(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	devs, err := s.st.Devices()
	if err != nil {
		httpError(w, 500, err.Error())
		return
	}
	if devs == nil {
		devs = []protocol.Device{}
	}
	for i := range devs {
		devs[i].Online = s.hub.Online(devs[i].ID)
	}
	writeJSON(w, 200, devs)
}

func (s *Server) handlePatchMe(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	var req protocol.UpdateDeviceRequest
	if err := readJSON(r, &req); err != nil {
		httpError(w, 400, "bad json")
		return
	}
	if err := s.st.UpdateDevice(d.ID, req.Name, req.FCMToken); err != nil {
		if errors.Is(err, store.ErrConflict) {
			httpError(w, 409, "name invalid or in use")
			return
		}
		httpError(w, 500, err.Error())
		return
	}
	w.WriteHeader(204)
}

func (s *Server) handleDeleteMe(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	if err := s.st.DeleteDevice(d.ID); err != nil {
		httpError(w, 500, err.Error())
		return
	}
	s.cancelSenderTransfers(d.ID)
	w.WriteHeader(204)
}
