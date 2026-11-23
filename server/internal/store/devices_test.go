package store

import (
	"errors"
	"strings"
	"testing"
	"time"

	"share/internal/protocol"
)

func mustOpen(t *testing.T) *Store {
	t.Helper()
	s, err := Open(":memory:")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	return s
}

func mustPair(t *testing.T, s *Store, name string, typ protocol.DeviceType) (string, string) {
	t.Helper()
	now := time.Now()
	code, err := s.CreatePairCode(now)
	if err != nil {
		t.Fatal(err)
	}
	id, tok, err := s.RedeemPairCode(code, now, name, typ, "fp-"+name)
	if err != nil {
		t.Fatal(err)
	}
	return id, tok
}

func TestPairCodeSingleUseAndExpiry(t *testing.T) {
	s := mustOpen(t)
	now := time.Now()
	code, err := s.CreatePairCode(now)
	if err != nil {
		t.Fatal(err)
	}
	if len(code) != 6 {
		t.Fatal(code)
	}
	id, tok, err := s.RedeemPairCode(strings.ToLower(code), now, "Pixel", protocol.DeviceAndroid, "fp")
	if err != nil || id == "" || tok == "" {
		t.Fatal(err)
	}
	if _, _, err := s.RedeemPairCode(code, now, "Other", protocol.DeviceAndroid, "fp"); !errors.Is(err, ErrInvalidCode) {
		t.Fatal("reuse allowed", err)
	}
	c2, _ := s.CreatePairCode(now)
	if _, _, err := s.RedeemPairCode(c2, now.Add(11*time.Minute), "Late", protocol.DeviceAndroid, "fp"); !errors.Is(err, ErrInvalidCode) {
		t.Fatal("expired code accepted", err)
	}
}

func TestDuplicateNameRejected(t *testing.T) {
	s := mustOpen(t)
	mustPair(t, s, "Pixel", protocol.DeviceAndroid)
	now := time.Now()
	code, _ := s.CreatePairCode(now)
	if _, _, err := s.RedeemPairCode(code, now, "pixel", protocol.DeviceAndroid, "fp"); !errors.Is(err, ErrConflict) {
		t.Fatal("duplicate name accepted", err)
	}
	// code was not consumed by the failed attempt
	if _, _, err := s.RedeemPairCode(code, now, "Pixel 2", protocol.DeviceAndroid, "fp"); err != nil {
		t.Fatal(err)
	}
}

func TestDeviceByToken(t *testing.T) {
	s := mustOpen(t)
	id, tok := mustPair(t, s, "Desk", protocol.DeviceWindows)
	d, err := s.DeviceByToken(tok)
	if err != nil || d.ID != id || d.Name != "Desk" || d.Type != protocol.DeviceWindows || d.CertFingerprint != "fp-Desk" {
		t.Fatal(d, err)
	}
	if _, err := s.DeviceByToken("wrong"); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
	if err := s.DeleteDevice(id); err != nil {
		t.Fatal(err)
	}
	if _, err := s.DeviceByToken(tok); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
}

func TestUpdateDeviceAndFCM(t *testing.T) {
	s := mustOpen(t)
	id, _ := mustPair(t, s, "Desk", protocol.DeviceWindows)
	n, f := "Desk 2", "fcm-1"
	if err := s.UpdateDevice(id, &n, &f); err != nil {
		t.Fatal(err)
	}
	d, _ := s.Device(id)
	if d.Name != "Desk 2" {
		t.Fatal(d.Name)
	}
	if tok, _ := s.FCMToken(id); tok != "fcm-1" {
		t.Fatal(tok)
	}
	if d2, err := s.DeviceByName("DESK 2"); err != nil || d2.ID != id {
		t.Fatal(err)
	}
	all, _ := s.Devices()
	if len(all) != 1 {
		t.Fatal(len(all))
	}
}
