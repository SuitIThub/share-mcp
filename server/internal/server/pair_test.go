package server

import (
	"testing"

	"share/internal/protocol"
)

func TestUnauthenticatedRejected(t *testing.T) {
	e := newTestServer(t)
	if st := e.do(t, "", "GET", "/v1/devices", nil, nil); st != 401 {
		t.Fatal(st)
	}
	if st := e.do(t, "bogus", "GET", "/v1/devices", nil, nil); st != 401 {
		t.Fatal(st)
	}
}

func TestPairFlow(t *testing.T) {
	e := newTestServer(t)
	id, tok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	var devs []protocol.Device
	if st := e.do(t, tok, "GET", "/v1/devices", nil, &devs); st != 200 {
		t.Fatal(st)
	}
	if len(devs) != 1 || devs[0].ID != id || devs[0].Name != "Pixel" {
		t.Fatal(devs)
	}
}

func TestPairErrors(t *testing.T) {
	e := newTestServer(t)
	e.pair(t, "Pixel", protocol.DeviceAndroid)
	code, _ := e.st.CreatePairCode(e.clock.Now())
	if st := e.do(t, "", "POST", "/v1/pair", protocol.PairRequest{Code: "XXXXXX", Name: "A", Type: "android"}, nil); st != 403 {
		t.Fatal("bad code", st)
	}
	if st := e.do(t, "", "POST", "/v1/pair", protocol.PairRequest{Code: code, Name: "A", Type: "toaster"}, nil); st != 400 {
		t.Fatal("bad type", st)
	}
	if st := e.do(t, "", "POST", "/v1/pair", protocol.PairRequest{Code: code, Name: " ", Type: "android"}, nil); st != 400 {
		t.Fatal("empty name", st)
	}
	if st := e.do(t, "", "POST", "/v1/pair", protocol.PairRequest{Code: code, Name: "pixel", Type: "android"}, nil); st != 409 {
		t.Fatal("dup name", st)
	}
}

func TestPairedDeviceCanCreateCode(t *testing.T) {
	e := newTestServer(t)
	_, tok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	var pc protocol.PairCodeResponse
	if st := e.do(t, tok, "POST", "/v1/pair/codes", nil, &pc); st != 200 || len(pc.Code) != 6 {
		t.Fatal(st, pc)
	}
	var resp protocol.PairResponse
	if st := e.do(t, "", "POST", "/v1/pair", protocol.PairRequest{Code: pc.Code, Name: "Desk", Type: "windows"}, &resp); st != 200 || resp.Token == "" {
		t.Fatal(st)
	}
}

func TestPatchAndDeleteMe(t *testing.T) {
	e := newTestServer(t)
	id, tok := e.pair(t, "Pixel", protocol.DeviceAndroid)
	n, f := "Pixel 9", "fcm"
	if st := e.do(t, tok, "PATCH", "/v1/devices/me", protocol.UpdateDeviceRequest{Name: &n, FCMToken: &f}, nil); st != 204 {
		t.Fatal(st)
	}
	if ft, _ := e.st.FCMToken(id); ft != "fcm" {
		t.Fatal(ft)
	}
	if st := e.do(t, tok, "DELETE", "/v1/devices/me", nil, nil); st != 204 {
		t.Fatal(st)
	}
	if st := e.do(t, tok, "GET", "/v1/devices", nil, nil); st != 401 {
		t.Fatal(st)
	}
}
