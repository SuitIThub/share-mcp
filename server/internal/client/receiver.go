package client

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"time"

	"share/internal/protocol"
	"share/internal/tlsid"
)

// Receiver accepts offers and downloads files, LAN first with relay fallback and resume.
type Receiver struct {
	api          *API
	dir          string
	probeTimeout time.Duration
	idleTimeout  time.Duration
	maxFailures  int
}

func NewReceiver(api *API, downloadDir string) *Receiver {
	return &Receiver{api: api, dir: downloadDir, probeTimeout: 1500 * time.Millisecond, idleTimeout: 30 * time.Second, maxFailures: 5}
}

func (r *Receiver) SetProbeTimeout(d time.Duration) { r.probeTimeout = d }

func (r *Receiver) Incoming(ctx context.Context) ([]protocol.Transfer, error) {
	return r.api.Incoming(ctx)
}

func (r *Receiver) Decline(ctx context.Context, id string) error { return r.api.Decline(ctx, id) }

// destPaths returns the temporary .part path and the (currently free) final path for file n.
func (r *Receiver) destPaths(transferID string, n int, name string) (part, final string) {
	safe := SafeFileName(name)
	tid := transferID
	if len(tid) > 8 {
		tid = tid[:8]
	}
	part = filepath.Join(r.dir, fmt.Sprintf("%s.%s-%d.part", safe, tid, n))
	return part, UniquePath(r.dir, safe)
}

// fatalError aborts the download without further retries.
type fatalError struct{ err error }

func (f fatalError) Error() string { return f.err.Error() }
func (f fatalError) Unwrap() error { return f.err }

// Accept accepts the offer. For text/link it returns (nil, nil): the content is t.Text.
// For files it downloads all files into the download dir and returns their final paths.
// progress (may be nil) receives the total received bytes and the current path.
func (r *Receiver) Accept(ctx context.Context, t protocol.Transfer, progress func(bytes int64, path protocol.Path)) ([]string, error) {
	ar, err := r.api.Accept(ctx, t.ID)
	if err != nil {
		return nil, err
	}
	if t.Kind != protocol.KindFiles {
		return nil, nil
	}
	if err := os.MkdirAll(r.dir, 0755); err != nil {
		return nil, err
	}
	rep := &reporter{api: r.api, id: t.ID, cb: progress}
	var done int64
	var out []string
	for n, f := range t.Files {
		part, _ := r.destPaths(t.ID, n, f.Name)
		if err := r.downloadFile(ctx, t, ar, n, part, done, rep); err != nil {
			if ctx.Err() == nil {
				r.api.Fail(context.Background(), t.ID)
			}
			return out, err
		}
		_, final := r.destPaths(t.ID, n, f.Name)
		if err := os.Rename(part, final); err != nil {
			return out, err
		}
		out = append(out, final)
		done += f.Size
	}
	rep.flush(done)
	if err := r.api.Delivered(ctx, t.ID); err != nil {
		return out, err
	}
	return out, nil
}

// reporter forwards every progress step to the callback and at most 1/s to the server.
type reporter struct {
	api  *API
	id   string
	cb   func(int64, protocol.Path)
	mu   sync.Mutex
	last time.Time
	path protocol.Path
}

func (p *reporter) report(total int64, path protocol.Path) {
	if p.cb != nil {
		p.cb(total, path)
	}
	p.mu.Lock()
	p.path = path
	due := time.Since(p.last) >= time.Second
	if due {
		p.last = time.Now()
	}
	p.mu.Unlock()
	if due {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		p.api.Progress(ctx, p.id, protocol.ProgressRequest{Bytes: total, Path: path})
		cancel()
	}
}

func (p *reporter) flush(total int64) {
	p.mu.Lock()
	path := p.path
	p.mu.Unlock()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	p.api.Progress(ctx, p.id, protocol.ProgressRequest{Bytes: total, Path: path})
}

func partSize(path string) int64 {
	st, err := os.Stat(path)
	if err != nil {
		return 0
	}
	return st.Size()
}

func (r *Receiver) downloadFile(ctx context.Context, t protocol.Transfer, ar protocol.AcceptResponse, n int, part string, before int64, rep *reporter) error {
	size := t.Files[n].Size
	failures := 0
	backoff := time.Second
	for {
		have := partSize(part)
		if have > size { // corrupt leftover
			os.Remove(part)
			have = 0
		}
		if have == size {
			if size == 0 {
				f, err := os.OpenFile(part, os.O_CREATE|os.O_WRONLY, 0644)
				if err != nil {
					return err
				}
				f.Close()
			}
			return nil
		}
		path, lanURL, client := r.choosePath(ctx, t.ID, n, ar)
		err := r.fetch(ctx, t.ID, n, ar, path, lanURL, client, part, have, func(got int64) { rep.report(before+got, path) })
		if err == nil {
			continue // loop re-checks size
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
		var fe fatalError
		if errors.As(err, &fe) {
			return fe.err
		}
		if partSize(part) > have {
			failures, backoff = 0, time.Second
		}
		failures++
		if failures >= r.maxFailures {
			return fmt.Errorf("download failed after %d attempts: %w", failures, err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(backoff):
		}
		backoff *= 2
	}
}

// choosePath probes all LAN candidates in parallel; the first answering one wins, otherwise relay.
func (r *Receiver) choosePath(ctx context.Context, id string, n int, ar protocol.AcceptResponse) (protocol.Path, string, *http.Client) {
	if len(ar.LANCandidates) == 0 || ar.CertFingerprint == "" {
		return protocol.PathRelay, "", nil
	}
	client := &http.Client{Transport: &http.Transport{
		TLSClientConfig:       tlsid.PinnedClientConfig(ar.CertFingerprint),
		ResponseHeaderTimeout: 15 * time.Second,
	}}
	pctx, cancel := context.WithTimeout(ctx, r.probeTimeout)
	defer cancel()
	winner := make(chan string, len(ar.LANCandidates))
	var wg sync.WaitGroup
	for _, c := range ar.LANCandidates {
		wg.Add(1)
		go func(c string) {
			defer wg.Done()
			req, _ := http.NewRequestWithContext(pctx, "GET", "https://"+c+"/t/"+id+"/ping", nil)
			req.Header.Set("Authorization", "Bearer "+ar.AccessToken)
			resp, err := client.Do(req)
			if err != nil {
				return
			}
			resp.Body.Close()
			if resp.StatusCode == 204 {
				winner <- c
			}
		}(c)
	}
	go func() { wg.Wait(); close(winner) }()
	if c, ok := <-winner; ok {
		return protocol.PathLAN, fmt.Sprintf("https://%s/t/%s/files/%d", c, id, n), client
	}
	return protocol.PathRelay, "", nil
}

// fetch downloads from offset `have` into part via the chosen path.
func (r *Receiver) fetch(ctx context.Context, id string, n int, ar protocol.AcceptResponse, path protocol.Path, lanURL string, client *http.Client, part string, have int64, onProgress func(int64)) error {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	var body io.ReadCloser
	if path == protocol.PathLAN {
		req, _ := http.NewRequestWithContext(ctx, "GET", lanURL, nil)
		req.Header.Set("Authorization", "Bearer "+ar.AccessToken)
		if have > 0 {
			req.Header.Set("Range", fmt.Sprintf("bytes=%d-", have))
		}
		resp, err := client.Do(req)
		if err != nil {
			return err
		}
		switch {
		case resp.StatusCode == 409:
			resp.Body.Close()
			return fatalError{errors.New("file changed on sender since it was offered")}
		case (have == 0 && resp.StatusCode == 200) || (have > 0 && resp.StatusCode == 206):
			body = resp.Body
		default:
			resp.Body.Close()
			return fmt.Errorf("local endpoint: status %d", resp.StatusCode)
		}
	} else {
		b, err := r.api.RelayDownload(ctx, id, n, have)
		if err != nil {
			var ae *APIError
			if errors.As(err, &ae) && (ae.Status == 403 || ae.Status == 404 || ae.Status == 410) {
				return fatalError{err}
			}
			return err
		}
		body = b
	}
	defer body.Close()
	f, err := os.OpenFile(part, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		return fatalError{err}
	}
	defer f.Close()
	// watchdog: abort if no data arrives for idleTimeout
	wd := time.AfterFunc(r.idleTimeout, cancel)
	defer wd.Stop()
	buf := make([]byte, 256*1024)
	got := have
	for {
		k, rerr := body.Read(buf)
		if k > 0 {
			wd.Reset(r.idleTimeout)
			if _, werr := f.Write(buf[:k]); werr != nil {
				return fatalError{werr}
			}
			got += int64(k)
			onProgress(got)
		}
		if rerr == io.EOF {
			return nil
		}
		if rerr != nil {
			return rerr
		}
	}
}
