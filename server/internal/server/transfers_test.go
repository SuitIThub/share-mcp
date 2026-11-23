package server

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"strings"
	"sync"
	"testing"
	"time"

	"share/internal/protocol"
)

type fakeNotifier struct {
	mu    sync.Mutex
	calls []map[string]string
	err   error
}

func (f *fakeNotifier) Notify(_ context.Context, tok string, data map[string]string) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	c := map[string]string{"token": tok}
	for k, v := range data {
		c[k] = v
	}
	f.calls = append(f.calls, c)
	return f.err
}

func (f *fakeNotifier) count() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.calls)
}

func fileReq(targets ...string) protocol.CreateTransferRequest {
	return protocol.CreateTransferRequest{
		Kind:    protocol.KindFiles,
		Files:   []protocol.FileItem{{Name: "app.apk", Size: 100, MIME: "application/vnd.android.package-archive"}},
		Targets: targets, LANCandidates: []string{"192.168.1.10:47800"},
	}
}

func (e *testEnv) create(t *testing.T, tok string, req protocol.CreateTransferRequest) protocol.CreateTransferResponse {
	t.Helper()
	var resp protocol.CreateTransferResponse
	if st := e.do(t, tok, "POST", "/v1/transfers", req, &resp); st != 201 {
		t.Fatalf("create status %d", st)
	}
	return resp
}

func delivery(t *testing.T, e *testEnv, tid, dev string) protocol.Delivery {
	t.Helper()
	d, err := e.st.Delivery(tid, dev)
	if err != nil {
		t.Fatal(err)
	}
	return d
}

func TestCreateTargetedNotifiesReceiver(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Agent", protocol.DeviceAgent)
	rid, rtok := e.pair(t, "Desk", protocol.DeviceWindows)
	ch, _ := e.ws(t, rtok)
	eventually(t, 2*time.Second, func() bool { return e.srv.hub.Online(rid) })
	resp := e.create(t, stok, fileReq("Desk"))
	ev := waitEvent(t, ch, protocol.EvOfferNew)
	var tr protocol.Transfer
	json.Unmarshal(ev.Data, &tr)
	if tr.ID != resp.Transfer.ID || len(tr.Deliveries) != 0 {
		t.Fatal(tr)
	}
	if b, _ := hex.DecodeString(resp.Secret); len(b) != 32 {
		t.Fatal("secret", resp.Secret)
	}
}

func TestTargetsByIDOrName(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Agent", protocol.DeviceAgent)
	rid, _ := e.pair(t, "Desk", protocol.DeviceWindows)
	resp := e.create(t, stok, fileReq(rid))
	if len(resp.Transfer.Deliveries) != 1 || resp.Transfer.Deliveries[0].DeviceID != rid {
		t.Fatal(resp.Transfer.Deliveries)
	}
}

func TestBroadcastExcludesAgentsAndSender(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Agent", protocol.DeviceAgent)
	e.pair(t, "Pixel", protocol.DeviceAndroid)
	e.pair(t, "Tablet", protocol.DeviceAndroid)
	e.pair(t, "Agent2", protocol.DeviceAgent)
	req := fileReq()
	req.Broadcast = true
	resp := e.create(t, stok, req)
	if len(resp.Transfer.Deliveries) != 2 {
		t.Fatal(resp.Transfer.Deliveries)
	}
}

func TestCreateValidation(t *testing.T) {
	e := newTestServer(t)
	sid, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	e.pair(t, "Agent", protocol.DeviceAgent)
	e.pair(t, "Pixel", protocol.DeviceAndroid)
	cases := map[string]protocol.CreateTransferRequest{
		"unknown target": fileReq("Nope"),
		"agent target":   fileReq("Agent"),
		"self target":    fileReq(sid),
		"no targets":     fileReq(),
		"no files":       {Kind: protocol.KindFiles, Targets: []string{"Pixel"}},
		"bad kind":       {Kind: "x", Targets: []string{"Pixel"}},
	}
	for name, req := range cases {
		if st := e.do(t, stok, "POST", "/v1/transfers", req, nil); st != 400 {
			t.Errorf("%s: status %d", name, st)
		}
	}
}

func TestMinutesClamped(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	e.pair(t, "Pixel", protocol.DeviceAndroid)
	req := fileReq("Pixel")
	req.Minutes = 60
	resp := e.create(t, stok, req)
	if !resp.Transfer.ExpiresAt.Equal(e.clock.Now().Add(10 * time.Minute)) {
		t.Fatal(resp.Transfer.ExpiresAt)
	}
	req.Minutes = 3
	resp = e.create(t, stok, req)
	if !resp.Transfer.ExpiresAt.Equal(e.clock.Now().Add(3 * time.Minute)) {
		t.Fatal(resp.Transfer.ExpiresAt)
	}
}

func TestTextTooLarge(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	e.pair(t, "Pixel", protocol.DeviceAndroid)
	req := protocol.CreateTransferRequest{Kind: protocol.KindText, Targets: []string{"Pixel"}, Text: strings.Repeat("a", 65537)}
	if st := e.do(t, stok, "POST", "/v1/transfers", req, nil); st != 413 {
		t.Fatal(st)
	}
	req.Text = strings.Repeat("a", 65536)
	if st := e.do(t, stok, "POST", "/v1/transfers", req, nil); st != 201 {
		t.Fatal(st)
	}
}

func TestAcceptReturnsValidToken(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	resp := e.create(t, stok, fileReq("Pixel"))
	var ar protocol.AcceptResponse
	if st := e.do(t, rtok, "POST", "/v1/transfers/"+resp.Transfer.ID+"/accept", nil, &ar); st != 200 {
		t.Fatal(st)
	}
	sec, _ := hex.DecodeString(resp.Secret)
	if id, ok := protocol.VerifyAccessToken(sec, resp.Transfer.ID, ar.AccessToken); !ok || id != rid {
		t.Fatal("token invalid")
	}
	if ar.CertFingerprint != "fp-Desk" || len(ar.LANCandidates) != 1 || ar.RelayBase != "/v1/transfers/"+resp.Transfer.ID+"/files/" {
		t.Fatal(ar)
	}
	if d := delivery(t, e, resp.Transfer.ID, rid); d.Status != protocol.StatusAccepted {
		t.Fatal(d)
	}
}

func TestAcceptRules(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	r2id, r2tok := e.pair(t, "Tablet", protocol.DeviceAndroid)
	_, otok := e.pair(t, "Outsider", protocol.DeviceAndroid)
	e.ws(t, stok) // sender stays online
	resp := e.create(t, stok, fileReq("Pixel", "Tablet"))
	id := resp.Transfer.ID
	var a1, a2 protocol.AcceptResponse
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, &a1)
	if st := e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, &a2); st != 200 || a1.AccessToken != a2.AccessToken {
		t.Fatal("second accept", st)
	}
	if st := e.do(t, rtok, "POST", "/v1/transfers/"+id+"/decline", nil, nil); st != 409 {
		t.Fatal("decline after accept", st)
	}
	if st := e.do(t, otok, "POST", "/v1/transfers/"+id+"/accept", nil, nil); st != 404 {
		t.Fatal("outsider accept", st)
	}
	e.clock.Advance(9 * time.Minute)
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/progress", protocol.ProgressRequest{Bytes: 5, Path: protocol.PathLAN}, nil)
	e.clock.Advance(2 * time.Minute)
	e.srv.tick(context.Background())
	if st := e.do(t, r2tok, "POST", "/v1/transfers/"+id+"/accept", nil, nil); st != 409 {
		t.Fatal("late accept", st)
	}
	if d := delivery(t, e, id, r2id); d.Status != protocol.StatusMissed {
		t.Fatal(d)
	}
	// already accepted receiver may re-fetch its token after expiry (resume)
	if st := e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, &a2); st != 200 || a2.AccessToken != a1.AccessToken {
		t.Fatal("resume accept", st)
	}
	_ = rid
}

func TestProgressDeliveredFail(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	sch, _ := e.ws(t, stok)
	resp := e.create(t, stok, fileReq("Pixel"))
	id := resp.Transfer.ID
	if st := e.do(t, rtok, "POST", "/v1/transfers/"+id+"/progress", protocol.ProgressRequest{Bytes: 1}, nil); st != 409 {
		t.Fatal("progress before accept", st)
	}
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, nil)
	if st := e.do(t, rtok, "POST", "/v1/transfers/"+id+"/progress", protocol.ProgressRequest{Bytes: 50, Path: protocol.PathLAN}, nil); st != 204 {
		t.Fatal(st)
	}
	d := delivery(t, e, id, rid)
	if d.Status != protocol.StatusDownloading || d.Bytes != 50 || d.Path != protocol.PathLAN {
		t.Fatal(d)
	}
	if st := e.do(t, rtok, "POST", "/v1/transfers/"+id+"/delivered", nil, nil); st != 204 {
		t.Fatal(st)
	}
	waitEvent(t, sch, protocol.EvDeliveryUpdated)
	waitEvent(t, sch, protocol.EvTransferClosed)
}

func TestTextAcceptIsDelivered(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	resp := e.create(t, stok, protocol.CreateTransferRequest{Kind: protocol.KindLink, Text: "https://example.org", Targets: []string{"Pixel"}})
	var in []protocol.Transfer
	e.do(t, rtok, "GET", "/v1/transfers?role=incoming", nil, &in)
	if len(in) != 1 || in[0].Text != "https://example.org" {
		t.Fatal(in)
	}
	e.do(t, rtok, "POST", "/v1/transfers/"+resp.Transfer.ID+"/accept", nil, nil)
	if d := delivery(t, e, resp.Transfer.ID, rid); d.Status != protocol.StatusDelivered {
		t.Fatal(d)
	}
}

func TestTargetedClosesWhenAllFinal(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	_, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	sch, _ := e.ws(t, stok)
	resp := e.create(t, stok, fileReq("Pixel"))
	e.do(t, rtok, "POST", "/v1/transfers/"+resp.Transfer.ID+"/decline", nil, nil)
	ev := waitEvent(t, sch, protocol.EvTransferClosed)
	var ref protocol.TransferRef
	json.Unmarshal(ev.Data, &ref)
	if ref.TransferID != resp.Transfer.ID {
		t.Fatal(ref)
	}
}

func TestBroadcastStaysOpenUntilExpiry(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	_, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	e.ws(t, stok)
	req := fileReq()
	req.Broadcast = true
	resp := e.create(t, stok, req)
	e.do(t, rtok, "POST", "/v1/transfers/"+resp.Transfer.ID+"/decline", nil, nil)
	e.srv.tick(context.Background())
	if tr, _ := e.st.Transfer(resp.Transfer.ID); tr.Closed {
		t.Fatal("closed early")
	}
	e.clock.Advance(10*time.Minute + time.Second)
	e.srv.tick(context.Background())
	if tr, _ := e.st.Transfer(resp.Transfer.ID); !tr.Closed {
		t.Fatal("not closed after expiry")
	}
}

func TestCancelNotifiesReceivers(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	rch, _ := e.ws(t, rtok)
	resp := e.create(t, stok, fileReq("Pixel"))
	if st := e.do(t, rtok, "DELETE", "/v1/transfers/"+resp.Transfer.ID, nil, nil); st != 404 {
		t.Fatal("receiver cancel", st)
	}
	if st := e.do(t, stok, "DELETE", "/v1/transfers/"+resp.Transfer.ID, nil, nil); st != 204 {
		t.Fatal(st)
	}
	waitEvent(t, rch, protocol.EvOfferRemoved)
	if d := delivery(t, e, resp.Transfer.ID, rid); d.Status != protocol.StatusCancelled {
		t.Fatal(d)
	}
}

// Review Focus 2: sender disappears during the offer.
func TestSenderDisconnectCancels(t *testing.T) {
	e := newTestServer(t)
	sid, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, _ := e.pair(t, "Pixel", protocol.DeviceAndroid)
	_, closeS := e.ws(t, stok)
	eventually(t, 2*time.Second, func() bool { return e.srv.hub.Online(sid) })
	resp := e.create(t, stok, fileReq("Pixel"))
	closeS()
	eventually(t, 2*time.Second, func() bool { return !e.srv.hub.Online(sid) })
	e.clock.Advance(10 * time.Second)
	e.srv.tick(context.Background())
	if d := delivery(t, e, resp.Transfer.ID, rid); d.Status != protocol.StatusWaiting {
		t.Fatal("cancelled within grace", d)
	}
	e.clock.Advance(21 * time.Second)
	e.srv.tick(context.Background())
	if d := delivery(t, e, resp.Transfer.ID, rid); d.Status != protocol.StatusCancelled {
		t.Fatal(d)
	}
}

func TestSenderReconnectWithinGraceKeepsOffer(t *testing.T) {
	e := newTestServer(t)
	sid, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, _ := e.pair(t, "Pixel", protocol.DeviceAndroid)
	_, closeS := e.ws(t, stok)
	eventually(t, 2*time.Second, func() bool { return e.srv.hub.Online(sid) })
	resp := e.create(t, stok, fileReq("Pixel"))
	closeS()
	eventually(t, 2*time.Second, func() bool { return !e.srv.hub.Online(sid) })
	e.clock.Advance(10 * time.Second)
	e.ws(t, stok)
	eventually(t, 2*time.Second, func() bool { return e.srv.hub.Online(sid) })
	e.clock.Advance(60 * time.Second)
	e.srv.tick(context.Background())
	if d := delivery(t, e, resp.Transfer.ID, rid); d.Status != protocol.StatusWaiting {
		t.Fatal(d)
	}
}

func TestStalledDownloadFails(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	e.ws(t, stok)
	resp := e.create(t, stok, fileReq("Pixel"))
	id := resp.Transfer.ID
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, nil)
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/progress", protocol.ProgressRequest{Bytes: 10, Path: protocol.PathRelay}, nil)
	e.clock.Advance(11 * time.Minute)
	e.srv.tick(context.Background())
	if d := delivery(t, e, id, rid); d.Status != protocol.StatusFailed {
		t.Fatal(d)
	}
}

func TestDownloadContinuesAfterExpiry(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	e.ws(t, stok)
	resp := e.create(t, stok, fileReq("Pixel"))
	id := resp.Transfer.ID
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/accept", nil, nil)
	e.clock.Advance(9 * time.Minute)
	e.do(t, rtok, "POST", "/v1/transfers/"+id+"/progress", protocol.ProgressRequest{Bytes: 10, Path: protocol.PathLAN}, nil)
	e.clock.Advance(2 * time.Minute) // expired, but progress 2 min ago < stall? exactly at boundary -> still ok
	e.srv.tick(context.Background())
	if d := delivery(t, e, id, rid); d.Status != protocol.StatusDownloading {
		t.Fatal(d)
	}
	if st := e.do(t, rtok, "POST", "/v1/transfers/"+id+"/delivered", nil, nil); st != 204 {
		t.Fatal(st)
	}
}

func TestNotifierCalledForAndroid(t *testing.T) {
	n := &fakeNotifier{}
	e := newTestServer(t, func(c *Config) { c.Notifier = n })
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	_, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	e.pair(t, "Tablet", protocol.DeviceAndroid) // no fcm token
	f := "fcm-pixel"
	e.do(t, rtok, "PATCH", "/v1/devices/me", protocol.UpdateDeviceRequest{FCMToken: &f}, nil)
	req := fileReq()
	req.Broadcast = true
	resp := e.create(t, stok, req)
	eventually(t, 2*time.Second, func() bool { return n.count() == 1 })
	n.mu.Lock()
	c := n.calls[0]
	n.mu.Unlock()
	if c["token"] != "fcm-pixel" || c["type"] != protocol.EvOfferNew || c["transfer_id"] != resp.Transfer.ID {
		t.Fatal(c)
	}
}

func TestReceiverViewHidesOtherDeliveries(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	e.pair(t, "Tablet", protocol.DeviceAndroid)
	resp := e.create(t, stok, fileReq("Pixel", "Tablet"))
	var tr protocol.Transfer
	if st := e.do(t, rtok, "GET", "/v1/transfers/"+resp.Transfer.ID, nil, &tr); st != 200 {
		t.Fatal(st)
	}
	if len(tr.Deliveries) != 1 || tr.Deliveries[0].DeviceID != rid {
		t.Fatal(tr.Deliveries)
	}
	var out []protocol.Transfer
	e.do(t, stok, "GET", "/v1/transfers?role=outgoing", nil, &out)
	if len(out) != 1 || len(out[0].Deliveries) != 2 {
		t.Fatal(out)
	}
}

// Final review #3: a connected sender must never count as gone.
func TestConnectedSenderNeverGone(t *testing.T) {
	e := newTestServer(t)
	sid, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, _ := e.pair(t, "Pixel", protocol.DeviceAndroid)
	e.ws(t, stok)
	eventually(t, 2*time.Second, func() bool { return e.srv.hub.Online(sid) })
	resp := e.create(t, stok, fileReq("Pixel"))
	e.srv.off.mu.Lock()
	e.srv.off.since[sid] = e.clock.Now().Add(-time.Hour) // stale entry from a callback race
	e.srv.off.mu.Unlock()
	e.srv.tick(context.Background())
	if d := delivery(t, e, resp.Transfer.ID, rid); d.Status != protocol.StatusWaiting {
		t.Fatal(d)
	}
}

// Final review #4: cancelling with a stale snapshot must not overwrite a finished delivery.
func TestCancelDoesNotOverwriteDelivered(t *testing.T) {
	e := newTestServer(t)
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	e.ws(t, stok)
	resp := e.create(t, stok, fileReq("Pixel"))
	stale, _ := e.st.Transfer(resp.Transfer.ID)
	e.do(t, rtok, "POST", "/v1/transfers/"+resp.Transfer.ID+"/accept", nil, nil)
	e.do(t, rtok, "POST", "/v1/transfers/"+resp.Transfer.ID+"/delivered", nil, nil)
	e.srv.mu.Lock()
	e.srv.cancelTransfer(stale)
	e.srv.mu.Unlock()
	if d := delivery(t, e, resp.Transfer.ID, rid); d.Status != protocol.StatusDelivered {
		t.Fatal(d)
	}
}
