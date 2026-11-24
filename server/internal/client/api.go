package client

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"

	"share/internal/protocol"
	"share/internal/tlsid"
)

type API struct {
	cfg  Config
	http *http.Client
}

type APIError struct {
	Status int
	Msg    string
}

func (e *APIError) Error() string { return fmt.Sprintf("server: %d %s", e.Status, e.Msg) }

func NewAPI(cfg Config) *API {
	cfg.ServerURL = strings.TrimRight(cfg.ServerURL, "/")
	return &API{cfg: cfg, http: &http.Client{}}
}

func (a *API) Config() Config { return a.cfg }

// Pair generates a device certificate and registers the device with a pairing code.
func Pair(ctx context.Context, serverURL, code, name string, typ protocol.DeviceType) (Config, error) {
	certPEM, keyPEM, err := tlsid.Generate(name)
	if err != nil {
		return Config{}, err
	}
	fp, _ := tlsid.FingerprintPEM(certPEM)
	a := NewAPI(Config{ServerURL: serverURL})
	var resp protocol.PairResponse
	if err := a.call(ctx, "POST", "/v1/pair", protocol.PairRequest{Code: code, Name: name, Type: typ, CertFingerprint: fp}, &resp); err != nil {
		return Config{}, err
	}
	return Config{ServerURL: a.cfg.ServerURL, DeviceID: resp.DeviceID, Token: resp.Token, CertPEM: certPEM, KeyPEM: keyPEM}, nil
}

func (a *API) request(ctx context.Context, method, path string, body io.Reader) (*http.Request, error) {
	req, err := http.NewRequestWithContext(ctx, method, a.cfg.ServerURL+path, body)
	if err != nil {
		return nil, err
	}
	if a.cfg.Token != "" {
		req.Header.Set("Authorization", "Bearer "+a.cfg.Token)
	}
	return req, nil
}

func apiError(resp *http.Response) error {
	var e struct {
		Error string `json:"error"`
	}
	b, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
	json.Unmarshal(b, &e)
	if e.Error == "" {
		e.Error = strings.TrimSpace(string(b))
	}
	return &APIError{Status: resp.StatusCode, Msg: e.Error}
}

func (a *API) call(ctx context.Context, method, path string, in, out any) error {
	var body io.Reader
	if in != nil {
		b, _ := json.Marshal(in)
		body = bytes.NewReader(b)
	}
	req, err := a.request(ctx, method, path, body)
	if err != nil {
		return err
	}
	if in != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	resp, err := a.http.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		return apiError(resp)
	}
	if out != nil {
		return json.NewDecoder(resp.Body).Decode(out)
	}
	return nil
}

func (a *API) Devices(ctx context.Context) ([]protocol.Device, error) {
	var out []protocol.Device
	return out, a.call(ctx, "GET", "/v1/devices", nil, &out)
}

func (a *API) CreateTransfer(ctx context.Context, req protocol.CreateTransferRequest) (protocol.CreateTransferResponse, error) {
	var out protocol.CreateTransferResponse
	return out, a.call(ctx, "POST", "/v1/transfers", req, &out)
}

func (a *API) Transfer(ctx context.Context, id string) (protocol.Transfer, error) {
	var out protocol.Transfer
	return out, a.call(ctx, "GET", "/v1/transfers/"+id, nil, &out)
}

func (a *API) Incoming(ctx context.Context) ([]protocol.Transfer, error) {
	var out []protocol.Transfer
	return out, a.call(ctx, "GET", "/v1/transfers?role=incoming", nil, &out)
}

func (a *API) Outgoing(ctx context.Context) ([]protocol.Transfer, error) {
	var out []protocol.Transfer
	return out, a.call(ctx, "GET", "/v1/transfers?role=outgoing", nil, &out)
}

func (a *API) Cancel(ctx context.Context, id string) error {
	return a.call(ctx, "DELETE", "/v1/transfers/"+id, nil, nil)
}

func (a *API) Accept(ctx context.Context, id string) (protocol.AcceptResponse, error) {
	var out protocol.AcceptResponse
	return out, a.call(ctx, "POST", "/v1/transfers/"+id+"/accept", nil, &out)
}

func (a *API) Decline(ctx context.Context, id string) error {
	return a.call(ctx, "POST", "/v1/transfers/"+id+"/decline", nil, nil)
}

func (a *API) Progress(ctx context.Context, id string, p protocol.ProgressRequest) error {
	return a.call(ctx, "POST", "/v1/transfers/"+id+"/progress", p, nil)
}

func (a *API) Delivered(ctx context.Context, id string) error {
	return a.call(ctx, "POST", "/v1/transfers/"+id+"/delivered", nil, nil)
}

func (a *API) Fail(ctx context.Context, id string) error {
	return a.call(ctx, "POST", "/v1/transfers/"+id+"/fail", nil, nil)
}

func (a *API) CreatePairCode(ctx context.Context) (string, error) {
	var out protocol.PairCodeResponse
	return out.Code, a.call(ctx, "POST", "/v1/pair/codes", nil, &out)
}

// RelayDownload streams file n from offset via the server relay.
func (a *API) RelayDownload(ctx context.Context, id string, n int, offset int64) (io.ReadCloser, error) {
	req, err := a.request(ctx, "GET", fmt.Sprintf("/v1/transfers/%s/files/%d", id, n), nil)
	if err != nil {
		return nil, err
	}
	if offset > 0 {
		req.Header.Set("Range", fmt.Sprintf("bytes=%d-", offset))
	}
	resp, err := a.http.Do(req)
	if err != nil {
		return nil, err
	}
	if resp.StatusCode != 200 && resp.StatusCode != 206 {
		defer resp.Body.Close()
		return nil, apiError(resp)
	}
	if offset > 0 && resp.StatusCode != 206 {
		resp.Body.Close()
		return nil, fmt.Errorf("relay ignored range")
	}
	return resp.Body, nil
}

// RelayUpload streams r to a waiting relay download.
func (a *API) RelayUpload(ctx context.Context, relayID string, r io.Reader) error {
	req, err := a.request(ctx, "POST", "/v1/relay/"+relayID, r)
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/octet-stream")
	resp, err := a.http.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		return apiError(resp)
	}
	return nil
}
