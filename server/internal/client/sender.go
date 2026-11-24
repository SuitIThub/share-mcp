package client

import (
	"context"
	"crypto/tls"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"mime"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"share/internal/lanaddr"
	"share/internal/protocol"
	"share/internal/tlsid"
)

// DefaultPort is the preferred port of the local endpoint.
const DefaultPort = 47800

type SenderOptions struct {
	Port       int                     // 0 = DefaultPort (falls back to a free port); <0 = any free port
	Candidates func(port int) []string // nil = lanaddr.Candidates
}

// Update reports a delivery change or the closing of one of our offers.
type Update struct {
	TransferID string
	Delivery   protocol.Delivery
	Closed     bool
}

type offer struct {
	transfer protocol.Transfer
	secret   []byte
	paths    []string
	stats    []os.FileInfo
}

// Sender offers files/text and serves them from a local HTTPS endpoint or via the relay.
type Sender struct {
	api  *API
	opts SenderOptions

	mu       sync.Mutex
	offers   map[string]*offer
	srv      *http.Server
	port     int
	noLocal  bool // test hook: local endpoint disabled
	updates  chan Update
	ready    chan struct{}
	readyOne sync.Once

	pending      int    // file offers between endpoint start and registration
	beforeCreate func() // test hook: runs between endpoint start and offer registration
}

func NewSender(api *API, opts SenderOptions) *Sender {
	if opts.Candidates == nil {
		opts.Candidates = lanaddr.Candidates
	}
	return &Sender{api: api, opts: opts, offers: map[string]*offer{}, updates: make(chan Update, 256), ready: make(chan struct{})}
}

// Updates delivers changes of our offers. Slow readers lose updates (never blocks the sender).
func (s *Sender) Updates() <-chan Update { return s.updates }

// WaitReady waits until the event connection is established.
func (s *Sender) WaitReady(ctx context.Context, d time.Duration) error {
	select {
	case <-s.ready:
		return nil
	case <-time.After(d):
		return errors.New("no connection to server")
	case <-ctx.Done():
		return ctx.Err()
	}
}

// Run processes server events until ctx is done. Offers end when Run ends.
func (s *Sender) Run(ctx context.Context) error {
	defer s.stopEndpoint()
	connects := 0
	for ev := range s.api.Events(ctx) {
		switch ev.Type {
		case EvConnected:
			connects++
			s.readyOne.Do(func() { close(s.ready) })
			if connects > 1 {
				go s.resync(ctx)
			}
		case protocol.EvRelayRequest:
			var rr protocol.RelayRequest
			if json.Unmarshal(ev.Data, &rr) == nil {
				go s.serveRelay(ctx, rr)
			}
		case protocol.EvDeliveryUpdated:
			var du protocol.DeliveryUpdate
			if json.Unmarshal(ev.Data, &du) == nil && s.has(du.TransferID) {
				s.emit(Update{TransferID: du.TransferID, Delivery: du.Delivery})
			}
		case protocol.EvTransferClosed:
			var ref protocol.TransferRef
			if json.Unmarshal(ev.Data, &ref) == nil && s.has(ref.TransferID) {
				s.remove(ref.TransferID)
				s.emit(Update{TransferID: ref.TransferID, Closed: true})
			}
		}
	}
	return ctx.Err()
}

// resync recovers events missed while disconnected: offers the server no longer lists as
// open are closed locally; current deliveries of open offers are re-emitted.
func (s *Sender) resync(ctx context.Context) {
	out, err := s.api.Outgoing(ctx)
	if err != nil {
		return
	}
	open := map[string]protocol.Transfer{}
	for _, t := range out {
		open[t.ID] = t
	}
	for _, id := range s.Active() {
		t, ok := open[id]
		if !ok {
			s.remove(id)
			s.emit(Update{TransferID: id, Closed: true})
			continue
		}
		for _, d := range t.Deliveries {
			s.emit(Update{TransferID: id, Delivery: d})
		}
	}
}

func (s *Sender) emit(u Update) {
	select {
	case s.updates <- u:
	default:
	}
}

func (s *Sender) has(id string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, ok := s.offers[id]
	return ok
}

// lookup waits briefly for an offer that may still be registering (server notifies receivers
// before our create call returns).
func (s *Sender) lookup(id string) *offer {
	for i := 0; i < 40; i++ {
		s.mu.Lock()
		o := s.offers[id]
		s.mu.Unlock()
		if o != nil {
			return o
		}
		time.Sleep(50 * time.Millisecond)
	}
	return nil
}

func (s *Sender) remove(id string) {
	s.mu.Lock()
	delete(s.offers, id)
	idle := s.pending == 0
	for _, o := range s.offers {
		if o.transfer.Kind == protocol.KindFiles {
			idle = false
		}
	}
	s.mu.Unlock()
	if idle {
		s.stopEndpoint()
	}
}

func mimeOf(name string) string {
	ext := strings.ToLower(filepath.Ext(name))
	if ext == ".apk" {
		return "application/vnd.android.package-archive"
	}
	if m := mime.TypeByExtension(ext); m != "" {
		return m
	}
	return "application/octet-stream"
}

// OfferFiles offers local files to targets (device names or IDs) or to all devices.
func (s *Sender) OfferFiles(ctx context.Context, paths []string, targets []string, broadcast bool, message string, minutes int) (protocol.Transfer, error) {
	if len(paths) == 0 {
		return protocol.Transfer{}, errors.New("no files given")
	}
	var items []protocol.FileItem
	var stats []os.FileInfo
	var abs []string
	for _, p := range paths {
		a, err := filepath.Abs(p)
		if err != nil {
			return protocol.Transfer{}, err
		}
		st, err := os.Stat(a)
		if err != nil {
			return protocol.Transfer{}, fmt.Errorf("file %s: %w", p, err)
		}
		if st.IsDir() {
			return protocol.Transfer{}, fmt.Errorf("file %s: is a directory", p)
		}
		abs = append(abs, a)
		stats = append(stats, st)
		items = append(items, protocol.FileItem{Name: st.Name(), Size: st.Size(), MIME: mimeOf(st.Name()), ModTime: st.ModTime()})
	}
	s.mu.Lock()
	s.pending++
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		s.pending--
		s.mu.Unlock()
		s.remove("") // stops the endpoint only if nothing uses it any more
	}()
	var cands []string
	if port, err := s.ensureEndpoint(); err == nil {
		cands = s.opts.Candidates(port)
	}
	if s.beforeCreate != nil {
		s.beforeCreate()
	}
	req := protocol.CreateTransferRequest{Kind: protocol.KindFiles, Files: items, Targets: targets, Broadcast: broadcast,
		Message: message, Minutes: minutes, LANCandidates: cands}
	resp, err := s.api.CreateTransfer(ctx, req)
	if err != nil {
		return protocol.Transfer{}, err
	}
	secret, _ := hex.DecodeString(resp.Secret)
	s.mu.Lock()
	s.offers[resp.Transfer.ID] = &offer{transfer: resp.Transfer, secret: secret, paths: abs, stats: stats}
	s.mu.Unlock()
	return resp.Transfer, nil
}

// OfferText offers a text or link.
func (s *Sender) OfferText(ctx context.Context, kind protocol.Kind, text string, targets []string, broadcast bool) (protocol.Transfer, error) {
	resp, err := s.api.CreateTransfer(ctx, protocol.CreateTransferRequest{Kind: kind, Text: text, Targets: targets, Broadcast: broadcast})
	if err != nil {
		return protocol.Transfer{}, err
	}
	s.mu.Lock()
	s.offers[resp.Transfer.ID] = &offer{transfer: resp.Transfer}
	s.mu.Unlock()
	return resp.Transfer, nil
}

// Cancel withdraws an offer.
func (s *Sender) Cancel(ctx context.Context, id string) error {
	err := s.api.Cancel(ctx, id)
	if s.has(id) {
		s.remove(id)
		s.emit(Update{TransferID: id, Closed: true})
	}
	return err
}

// Active returns the IDs of offers that are still open.
func (s *Sender) Active() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []string
	for id := range s.offers {
		out = append(out, id)
	}
	return out
}

func (s *Sender) ensureEndpoint() (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.noLocal {
		return 0, errors.New("local endpoint disabled")
	}
	if s.srv != nil {
		return s.port, nil
	}
	cfg := s.api.Config()
	tcfg, err := tlsid.ServerConfig(cfg.CertPEM, cfg.KeyPEM)
	if err != nil {
		return 0, err
	}
	var ln net.Listener
	switch {
	case s.opts.Port < 0:
		ln, err = net.Listen("tcp", ":0")
	default:
		port := s.opts.Port
		if port == 0 {
			port = DefaultPort
		}
		ln, err = net.Listen("tcp", ":"+strconv.Itoa(port))
		if err != nil {
			ln, err = net.Listen("tcp", ":0")
		}
	}
	if err != nil {
		return 0, err
	}
	s.port = ln.Addr().(*net.TCPAddr).Port
	mux := http.NewServeMux()
	mux.HandleFunc("GET /t/{id}/ping", s.handlePing)
	mux.HandleFunc("GET /t/{id}/files/{n}", s.handleFile)
	s.srv = &http.Server{Handler: mux, ReadHeaderTimeout: 10 * time.Second}
	go s.srv.Serve(tls.NewListener(ln, tcfg))
	return s.port, nil
}

func (s *Sender) stopEndpoint() {
	s.mu.Lock()
	srv := s.srv
	s.srv = nil
	s.mu.Unlock()
	if srv != nil {
		srv.Close()
	}
}

// dropLocalEndpoint is a test hook simulating loss of the LAN path.
func (s *Sender) dropLocalEndpoint() {
	s.mu.Lock()
	s.noLocal = true
	s.mu.Unlock()
	s.stopEndpoint()
}

// authorize checks the bearer token against the offer; writes the error response itself.
func (s *Sender) authorize(w http.ResponseWriter, r *http.Request) *offer {
	id := r.PathValue("id")
	tok := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
	o := s.lookup(id)
	if o == nil || o.transfer.Kind != protocol.KindFiles {
		http.Error(w, "not found", 404)
		return nil
	}
	if _, ok := protocol.VerifyAccessToken(o.secret, id, tok); !ok {
		http.Error(w, "unauthorized", 401)
		return nil
	}
	return o
}

func (s *Sender) handlePing(w http.ResponseWriter, r *http.Request) {
	if s.authorize(w, r) != nil {
		w.WriteHeader(204)
	}
}

// openUnchanged opens file n if it still matches what was offered.
func (o *offer) openUnchanged(n int) (*os.File, error) {
	if n < 0 || n >= len(o.paths) {
		return nil, os.ErrNotExist
	}
	f, err := os.Open(o.paths[n])
	if err != nil {
		return nil, err
	}
	st, err := f.Stat()
	if err != nil || st.Size() != o.stats[n].Size() || !st.ModTime().Equal(o.stats[n].ModTime()) {
		f.Close()
		return nil, errModified
	}
	return f, nil
}

var errModified = errors.New("file changed since it was offered")

func (s *Sender) handleFile(w http.ResponseWriter, r *http.Request) {
	o := s.authorize(w, r)
	if o == nil {
		return
	}
	n, err := strconv.Atoi(r.PathValue("n"))
	if err != nil || n < 0 || n >= len(o.paths) {
		http.Error(w, "no such file", 404)
		return
	}
	f, err := o.openUnchanged(n)
	if errors.Is(err, errModified) {
		http.Error(w, err.Error(), 409)
		return
	}
	if err != nil {
		http.Error(w, "file unavailable", 410)
		return
	}
	defer f.Close()
	w.Header().Set("Content-Type", o.transfer.Files[n].MIME)
	http.ServeContent(w, r, "", o.stats[n].ModTime(), f)
}

func (s *Sender) serveRelay(ctx context.Context, rr protocol.RelayRequest) {
	o := s.lookup(rr.TransferID)
	if o == nil {
		return
	}
	f, err := o.openUnchanged(rr.FileIndex)
	if err != nil {
		return
	}
	defer f.Close()
	if _, err := f.Seek(rr.Offset, io.SeekStart); err != nil {
		return
	}
	s.api.RelayUpload(ctx, rr.RelayID, f)
}

// API returns the sender's API client.
func (s *Sender) API() *API { return s.api }
