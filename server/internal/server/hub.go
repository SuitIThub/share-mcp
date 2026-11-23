package server

import (
	"net/http"

	"share/internal/protocol"
)

type hub struct{}

func newHub() *hub                   { return &hub{} }
func (h *hub) Online(id string) bool { return false }
func (h *hub) closeAll()             {}

func (s *Server) handleEvents(w http.ResponseWriter, r *http.Request, d protocol.Device) {
	httpError(w, 501, "not implemented")
}
