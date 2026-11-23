package server

import (
	"testing"
	"time"

	"share/internal/protocol"
)

func TestTokenGoneClearsFCMToken(t *testing.T) {
	n := &fakeNotifier{err: ErrTokenGone}
	e := newTestServer(t, func(c *Config) { c.Notifier = n })
	_, stok := e.pair(t, "Desk", protocol.DeviceWindows)
	rid, rtok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	f := "stale"
	e.do(t, rtok, "PATCH", "/v1/devices/me", protocol.UpdateDeviceRequest{FCMToken: &f}, nil)
	e.create(t, stok, fileReq("Pixel"))
	eventually(t, 2*time.Second, func() bool { tok, _ := e.st.FCMToken(rid); return tok == "" })
}

func TestFCMMessageShape(t *testing.T) {
	m := fcmMessage("tok", map[string]string{"type": "offer.new", "transfer_id": "x"})
	if m.Token != "tok" || m.Android == nil || m.Android.Priority != "high" || m.Android.TTL == nil || *m.Android.TTL != 10*time.Minute || m.Data["transfer_id"] != "x" {
		t.Fatal(m)
	}
	if m.Notification != nil {
		t.Fatal("must be data-only message")
	}
}
