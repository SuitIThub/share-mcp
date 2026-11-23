package store

import (
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"errors"
	"strings"
	"time"

	"share/internal/protocol"
)

const codeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"

// PairCodeTTL is how long a pairing code stays valid.
const PairCodeTTL = 10 * time.Minute

func hashToken(tok string) string {
	h := sha256.Sum256([]byte(tok))
	return hex.EncodeToString(h[:])
}

// CreatePairCode creates a single-use code valid for PairCodeTTL.
func (s *Store) CreatePairCode(now time.Time) (string, error) {
	b := make([]byte, 6)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	for i := range b {
		b[i] = codeAlphabet[int(b[i])%len(codeAlphabet)]
	}
	code := string(b)
	_, err := s.db.Exec(`DELETE FROM pair_codes WHERE expires_at < ?`, ts(now))
	if err != nil {
		return "", err
	}
	_, err = s.db.Exec(`INSERT INTO pair_codes(code, expires_at) VALUES(?, ?)`, code, ts(now.Add(PairCodeTTL)))
	return code, err
}

// RedeemPairCode consumes a code and registers a new device.
func (s *Store) RedeemPairCode(code string, now time.Time, name string, typ protocol.DeviceType, fp string) (string, string, error) {
	code = strings.ToUpper(strings.TrimSpace(code))
	name = strings.TrimSpace(name)
	tx, err := s.db.Begin()
	if err != nil {
		return "", "", err
	}
	defer tx.Rollback()
	var exp int64
	err = tx.QueryRow(`SELECT expires_at FROM pair_codes WHERE code = ?`, code).Scan(&exp)
	if errors.Is(err, sql.ErrNoRows) || (err == nil && exp < ts(now)) {
		return "", "", ErrInvalidCode
	}
	if err != nil {
		return "", "", err
	}
	var n int
	if err := tx.QueryRow(`SELECT COUNT(*) FROM devices WHERE name_lc = ?`, strings.ToLower(name)).Scan(&n); err != nil {
		return "", "", err
	}
	if n > 0 {
		return "", "", ErrConflict
	}
	id := randomHex(8)
	tok := randomHex(32)
	if _, err := tx.Exec(`INSERT INTO devices(id, name, name_lc, type, token_hash, cert_fp, last_seen) VALUES(?,?,?,?,?,?,?)`,
		id, name, strings.ToLower(name), string(typ), hashToken(tok), fp, ts(now)); err != nil {
		return "", "", err
	}
	if _, err := tx.Exec(`DELETE FROM pair_codes WHERE code = ?`, code); err != nil {
		return "", "", err
	}
	return id, tok, tx.Commit()
}

const deviceCols = `id, name, type, cert_fp, last_seen`

func scanDevice(row interface{ Scan(...any) error }) (protocol.Device, error) {
	var d protocol.Device
	var typ string
	var last int64
	err := row.Scan(&d.ID, &d.Name, &typ, &d.CertFingerprint, &last)
	if errors.Is(err, sql.ErrNoRows) {
		return d, ErrNotFound
	}
	d.Type = protocol.DeviceType(typ)
	d.LastSeen = fromTS(last)
	return d, err
}

func (s *Store) DeviceByToken(token string) (protocol.Device, error) {
	return scanDevice(s.db.QueryRow(`SELECT `+deviceCols+` FROM devices WHERE token_hash = ?`, hashToken(token)))
}

func (s *Store) Device(id string) (protocol.Device, error) {
	return scanDevice(s.db.QueryRow(`SELECT `+deviceCols+` FROM devices WHERE id = ?`, id))
}

func (s *Store) DeviceByName(name string) (protocol.Device, error) {
	return scanDevice(s.db.QueryRow(`SELECT `+deviceCols+` FROM devices WHERE name_lc = ?`, strings.ToLower(strings.TrimSpace(name))))
}

func (s *Store) Devices() ([]protocol.Device, error) {
	rows, err := s.db.Query(`SELECT ` + deviceCols + ` FROM devices ORDER BY name_lc`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []protocol.Device
	for rows.Next() {
		d, err := scanDevice(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, d)
	}
	return out, rows.Err()
}

// UpdateDevice changes name and/or FCM token (nil = unchanged).
func (s *Store) UpdateDevice(id string, name *string, fcmToken *string) error {
	if name != nil {
		n := strings.TrimSpace(*name)
		if n == "" {
			return ErrConflict
		}
		var other string
		err := s.db.QueryRow(`SELECT id FROM devices WHERE name_lc = ?`, strings.ToLower(n)).Scan(&other)
		if err == nil && other != id {
			return ErrConflict
		}
		if _, err := s.db.Exec(`UPDATE devices SET name = ?, name_lc = ? WHERE id = ?`, n, strings.ToLower(n), id); err != nil {
			return err
		}
	}
	if fcmToken != nil {
		if _, err := s.db.Exec(`UPDATE devices SET fcm_token = ? WHERE id = ?`, *fcmToken, id); err != nil {
			return err
		}
	}
	return nil
}

func (s *Store) FCMToken(id string) (string, error) {
	var t string
	err := s.db.QueryRow(`SELECT fcm_token FROM devices WHERE id = ?`, id).Scan(&t)
	if errors.Is(err, sql.ErrNoRows) {
		return "", ErrNotFound
	}
	return t, err
}

func (s *Store) TouchDevice(id string, now time.Time) error {
	_, err := s.db.Exec(`UPDATE devices SET last_seen = ? WHERE id = ?`, ts(now), id)
	return err
}

func (s *Store) DeleteDevice(id string) error {
	res, err := s.db.Exec(`DELETE FROM devices WHERE id = ?`, id)
	if err != nil {
		return err
	}
	if n, _ := res.RowsAffected(); n == 0 {
		return ErrNotFound
	}
	return nil
}
