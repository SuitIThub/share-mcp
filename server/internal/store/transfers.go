package store

import (
	"database/sql"
	"encoding/json"
	"errors"
	"time"

	"share/internal/protocol"
)

// CreateTransfer stores offer metadata and a waiting delivery per recipient.
func (s *Store) CreateTransfer(t protocol.Transfer, secret []byte, lanCandidates []string, recipients []string) error {
	files, _ := json.Marshal(nonNil(t.Files))
	targets, _ := json.Marshal(nonNilS(t.Targets))
	lan, _ := json.Marshal(nonNilS(lanCandidates))
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()
	_, err = tx.Exec(`INSERT INTO transfers(id, sender_id, kind, text, message, files_json, broadcast, targets_json, secret, lan_json, created_at, expires_at)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?)`,
		t.ID, t.SenderID, string(t.Kind), t.Text, t.Message, string(files), t.Broadcast, string(targets), secret, string(lan), ts(t.CreatedAt), ts(t.ExpiresAt))
	if err != nil {
		return err
	}
	for _, r := range recipients {
		if _, err := tx.Exec(`INSERT INTO deliveries(transfer_id, device_id, status, updated_at) VALUES(?,?,?,?)`,
			t.ID, r, string(protocol.StatusWaiting), ts(t.CreatedAt)); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func nonNil(f []protocol.FileItem) []protocol.FileItem {
	if f == nil {
		return []protocol.FileItem{}
	}
	return f
}

func nonNilS(s []string) []string {
	if s == nil {
		return []string{}
	}
	return s
}

const transferCols = `id, sender_id, kind, text, message, files_json, broadcast, targets_json, created_at, expires_at, closed_at`

func scanTransfer(row interface{ Scan(...any) error }) (protocol.Transfer, error) {
	var t protocol.Transfer
	var kind, files, targets string
	var created, expires, closed int64
	err := row.Scan(&t.ID, &t.SenderID, &kind, &t.Text, &t.Message, &files, &t.Broadcast, &targets, &created, &expires, &closed)
	if errors.Is(err, sql.ErrNoRows) {
		return t, ErrNotFound
	}
	if err != nil {
		return t, err
	}
	t.Kind = protocol.Kind(kind)
	json.Unmarshal([]byte(files), &t.Files)
	json.Unmarshal([]byte(targets), &t.Targets)
	if len(t.Files) == 0 {
		t.Files = nil
	}
	if len(t.Targets) == 0 {
		t.Targets = nil
	}
	t.CreatedAt, t.ExpiresAt = fromTS(created), fromTS(expires)
	t.Closed = closed != 0
	return t, nil
}

func (s *Store) deliveries(transferID string) ([]protocol.Delivery, error) {
	rows, err := s.db.Query(`SELECT device_id, status, path, bytes, updated_at FROM deliveries WHERE transfer_id = ? ORDER BY device_id`, transferID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []protocol.Delivery
	for rows.Next() {
		var d protocol.Delivery
		var st, p string
		var up int64
		if err := rows.Scan(&d.DeviceID, &st, &p, &d.Bytes, &up); err != nil {
			return nil, err
		}
		d.Status, d.Path, d.UpdatedAt = protocol.Status(st), protocol.Path(p), fromTS(up)
		out = append(out, d)
	}
	return out, rows.Err()
}

// Transfer returns the transfer including all deliveries.
func (s *Store) Transfer(id string) (protocol.Transfer, error) {
	t, err := scanTransfer(s.db.QueryRow(`SELECT `+transferCols+` FROM transfers WHERE id = ?`, id))
	if err != nil {
		return t, err
	}
	t.Deliveries, err = s.deliveries(id)
	return t, err
}

func (s *Store) TransferSecret(id string) ([]byte, error) {
	var b []byte
	err := s.db.QueryRow(`SELECT secret FROM transfers WHERE id = ?`, id).Scan(&b)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, ErrNotFound
	}
	return b, err
}

func (s *Store) LANCandidates(id string) ([]string, error) {
	var j string
	err := s.db.QueryRow(`SELECT lan_json FROM transfers WHERE id = ?`, id).Scan(&j)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, ErrNotFound
	}
	var out []string
	json.Unmarshal([]byte(j), &out)
	return out, err
}

func (s *Store) queryTransfers(q string, args ...any) ([]protocol.Transfer, error) {
	rows, err := s.db.Query(q, args...)
	if err != nil {
		return nil, err
	}
	var out []protocol.Transfer
	for rows.Next() {
		t, err := scanTransfer(rows)
		if err != nil {
			rows.Close()
			return nil, err
		}
		out = append(out, t)
	}
	rows.Close()
	return out, rows.Err()
}

// Incoming lists open offers where the device has a non-final delivery. Deliveries are not included.
func (s *Store) Incoming(deviceID string) ([]protocol.Transfer, error) {
	return s.queryTransfers(`SELECT `+prefixed("t.", transferCols)+` FROM transfers t JOIN deliveries d ON d.transfer_id = t.id
		WHERE d.device_id = ? AND t.closed_at = 0 AND d.status IN ('waiting','accepted','downloading') ORDER BY t.created_at`, deviceID)
}

// Outgoing lists open offers of the sender including deliveries.
func (s *Store) Outgoing(senderID string) ([]protocol.Transfer, error) {
	out, err := s.queryTransfers(`SELECT `+transferCols+` FROM transfers WHERE sender_id = ? AND closed_at = 0 ORDER BY created_at`, senderID)
	if err != nil {
		return nil, err
	}
	for i := range out {
		if out[i].Deliveries, err = s.deliveries(out[i].ID); err != nil {
			return nil, err
		}
	}
	return out, nil
}

// OpenTransfers lists all open transfers including deliveries.
func (s *Store) OpenTransfers() ([]protocol.Transfer, error) {
	out, err := s.queryTransfers(`SELECT ` + transferCols + ` FROM transfers WHERE closed_at = 0`)
	if err != nil {
		return nil, err
	}
	for i := range out {
		if out[i].Deliveries, err = s.deliveries(out[i].ID); err != nil {
			return nil, err
		}
	}
	return out, nil
}

func (s *Store) Delivery(transferID, deviceID string) (protocol.Delivery, error) {
	var d protocol.Delivery
	var st, p string
	var up int64
	err := s.db.QueryRow(`SELECT device_id, status, path, bytes, updated_at FROM deliveries WHERE transfer_id = ? AND device_id = ?`, transferID, deviceID).
		Scan(&d.DeviceID, &st, &p, &d.Bytes, &up)
	if errors.Is(err, sql.ErrNoRows) {
		return d, ErrNotFound
	}
	d.Status, d.Path, d.UpdatedAt = protocol.Status(st), protocol.Path(p), fromTS(up)
	return d, err
}

func (s *Store) SetDelivery(transferID, deviceID string, st protocol.Status, path protocol.Path, bytes int64, now time.Time) error {
	res, err := s.db.Exec(`UPDATE deliveries SET status = ?, path = ?, bytes = ?, updated_at = ? WHERE transfer_id = ? AND device_id = ?`,
		string(st), string(path), bytes, ts(now), transferID, deviceID)
	if err != nil {
		return err
	}
	if n, _ := res.RowsAffected(); n == 0 {
		return ErrNotFound
	}
	return nil
}

func (s *Store) CloseTransfer(id string, now time.Time) error {
	_, err := s.db.Exec(`UPDATE transfers SET closed_at = ? WHERE id = ? AND closed_at = 0`, ts(now), id)
	return err
}

// PurgeClosedBefore deletes metadata of transfers closed before t.
func (s *Store) PurgeClosedBefore(t time.Time) error {
	_, err := s.db.Exec(`DELETE FROM deliveries WHERE transfer_id IN (SELECT id FROM transfers WHERE closed_at != 0 AND closed_at < ?)`, ts(t))
	if err != nil {
		return err
	}
	_, err = s.db.Exec(`DELETE FROM transfers WHERE closed_at != 0 AND closed_at < ?`, ts(t))
	return err
}

func prefixed(p, cols string) string {
	out := ""
	start := 0
	for i := 0; i <= len(cols); i++ {
		if i == len(cols) || cols[i] == ',' {
			c := cols[start:i]
			for len(c) > 0 && c[0] == ' ' {
				c = c[1:]
			}
			if out != "" {
				out += ", "
			}
			out += p + c
			start = i + 1
		}
	}
	return out
}
