package store

import (
	"errors"
	"testing"
	"time"

	"share/internal/protocol"
)

func newTransfer(id, sender string, now time.Time) protocol.Transfer {
	return protocol.Transfer{
		ID: id, SenderID: sender, Kind: protocol.KindFiles,
		Files:     []protocol.FileItem{{Name: "a.apk", Size: 10, MIME: "application/vnd.android.package-archive"}},
		CreatedAt: now, ExpiresAt: now.Add(10 * time.Minute),
	}
}

func TestIncomingOnlyNonFinal(t *testing.T) {
	s := mustOpen(t)
	now := time.Now()
	if err := s.CreateTransfer(newTransfer("t1", "snd", now), []byte("sec"), []string{"1.2.3.4:1"}, []string{"rcv"}); err != nil {
		t.Fatal(err)
	}
	in, err := s.Incoming("rcv")
	if err != nil || len(in) != 1 || in[0].ID != "t1" || in[0].Files[0].Name != "a.apk" {
		t.Fatal(in, err)
	}
	if len(in[0].Deliveries) != 0 {
		t.Fatal("receiver must not see deliveries")
	}
	if err := s.SetDelivery("t1", "rcv", protocol.StatusDeclined, "", 0, now); err != nil {
		t.Fatal(err)
	}
	in, _ = s.Incoming("rcv")
	if len(in) != 0 {
		t.Fatal("declined offer still incoming")
	}
}

func TestOutgoingHasDeliveries(t *testing.T) {
	s := mustOpen(t)
	now := time.Now()
	s.CreateTransfer(newTransfer("t1", "snd", now), []byte("sec"), nil, []string{"a", "b"})
	out, err := s.Outgoing("snd")
	if err != nil || len(out) != 1 || len(out[0].Deliveries) != 2 {
		t.Fatal(out, err)
	}
	for _, d := range out[0].Deliveries {
		if d.Status != protocol.StatusWaiting {
			t.Fatal(d)
		}
	}
	d, err := s.Delivery("t1", "a")
	if err != nil || d.Status != protocol.StatusWaiting {
		t.Fatal(d, err)
	}
	if _, err := s.Delivery("t1", "zzz"); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
	sec, _ := s.TransferSecret("t1")
	if string(sec) != "sec" {
		t.Fatal(sec)
	}
}

func TestClosedTransferHidden(t *testing.T) {
	s := mustOpen(t)
	now := time.Now()
	s.CreateTransfer(newTransfer("t1", "snd", now), []byte("sec"), nil, []string{"rcv"})
	if err := s.CloseTransfer("t1", now); err != nil {
		t.Fatal(err)
	}
	in, _ := s.Incoming("rcv")
	out, _ := s.Outgoing("snd")
	open, _ := s.OpenTransfers()
	if len(in)+len(out)+len(open) != 0 {
		t.Fatal("closed transfer visible")
	}
	tr, err := s.Transfer("t1")
	if err != nil || !tr.Closed {
		t.Fatal(tr, err)
	}
}

func TestPurge(t *testing.T) {
	s := mustOpen(t)
	now := time.Now()
	s.CreateTransfer(newTransfer("t1", "snd", now), []byte("sec"), nil, []string{"rcv"})
	s.CloseTransfer("t1", now)
	if err := s.PurgeClosedBefore(now.Add(2 * time.Hour)); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Transfer("t1"); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
}

func TestLANCandidatesAndText(t *testing.T) {
	s := mustOpen(t)
	now := time.Now()
	tr := protocol.Transfer{ID: "t2", SenderID: "snd", Kind: protocol.KindText, Text: "hallo", CreatedAt: now, ExpiresAt: now.Add(time.Minute)}
	s.CreateTransfer(tr, []byte("s"), []string{"10.0.0.2:47800"}, []string{"rcv"})
	c, _ := s.LANCandidates("t2")
	if len(c) != 1 || c[0] != "10.0.0.2:47800" {
		t.Fatal(c)
	}
	got, _ := s.Transfer("t2")
	if got.Text != "hallo" {
		t.Fatal(got)
	}
}
