package protocol

import (
	"bytes"
	"encoding/json"
	"testing"
	"time"
)

func TestStatusFinal(t *testing.T) {
	for _, s := range []Status{StatusDelivered, StatusDeclined, StatusMissed, StatusCancelled, StatusFailed} {
		if !s.Final() {
			t.Errorf("%s should be final", s)
		}
	}
	for _, s := range []Status{StatusWaiting, StatusAccepted, StatusDownloading} {
		if s.Final() {
			t.Errorf("%s should not be final", s)
		}
	}
}

func TestAccessTokenRoundTrip(t *testing.T) {
	sec := []byte("0123456789abcdef0123456789abcdef")
	tok := AccessToken(sec, "t1", "devA")
	id, ok := VerifyAccessToken(sec, "t1", tok)
	if !ok || id != "devA" {
		t.Fatal("valid token rejected")
	}
	if _, ok := VerifyAccessToken(sec, "t2", tok); ok {
		t.Fatal("token valid for other transfer")
	}
	if _, ok := VerifyAccessToken([]byte("other-secret-other-secret-000000"), "t1", tok); ok {
		t.Fatal("token valid with other secret")
	}
	if _, ok := VerifyAccessToken(sec, "t1", "devA.00"); ok {
		t.Fatal("garbage accepted")
	}
	if _, ok := VerifyAccessToken(sec, "t1", "nodot"); ok {
		t.Fatal("garbage accepted")
	}
}

func TestTransferJSONSnakeCase(t *testing.T) {
	b, _ := json.Marshal(Transfer{SenderID: "x", ExpiresAt: time.Unix(0, 0).UTC()})
	if !bytes.Contains(b, []byte(`"sender_id":"x"`)) || !bytes.Contains(b, []byte(`"expires_at"`)) {
		t.Fatal(string(b))
	}
}
