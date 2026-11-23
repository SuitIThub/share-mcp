// Package store persists devices, pairing codes and offer metadata in SQLite.
// It never stores file contents.
package store

import (
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"
	"time"

	_ "modernc.org/sqlite"
)

var (
	ErrNotFound    = errors.New("not found")
	ErrConflict    = errors.New("conflict")
	ErrInvalidCode = errors.New("invalid or expired pairing code")
)

type Store struct {
	db *sql.DB
}

const schema = `
CREATE TABLE IF NOT EXISTS devices (
	id TEXT PRIMARY KEY,
	name TEXT NOT NULL,
	name_lc TEXT NOT NULL UNIQUE,
	type TEXT NOT NULL,
	token_hash TEXT NOT NULL UNIQUE,
	cert_fp TEXT NOT NULL DEFAULT '',
	fcm_token TEXT NOT NULL DEFAULT '',
	last_seen INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS pair_codes (
	code TEXT PRIMARY KEY,
	expires_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS transfers (
	id TEXT PRIMARY KEY,
	sender_id TEXT NOT NULL,
	kind TEXT NOT NULL,
	text TEXT NOT NULL DEFAULT '',
	message TEXT NOT NULL DEFAULT '',
	files_json TEXT NOT NULL DEFAULT '[]',
	broadcast INTEGER NOT NULL DEFAULT 0,
	targets_json TEXT NOT NULL DEFAULT '[]',
	secret BLOB NOT NULL,
	lan_json TEXT NOT NULL DEFAULT '[]',
	created_at INTEGER NOT NULL,
	expires_at INTEGER NOT NULL,
	closed_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS deliveries (
	transfer_id TEXT NOT NULL REFERENCES transfers(id) ON DELETE CASCADE,
	device_id TEXT NOT NULL,
	status TEXT NOT NULL,
	path TEXT NOT NULL DEFAULT '',
	bytes INTEGER NOT NULL DEFAULT 0,
	updated_at INTEGER NOT NULL,
	PRIMARY KEY (transfer_id, device_id)
);
`

// Open opens (and migrates) the database at path. Use ":memory:" for tests.
func Open(path string) (*Store, error) {
	dsn := path
	if path != ":memory:" {
		dsn = "file:" + path + "?_pragma=journal_mode(WAL)&_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)"
	} else {
		dsn = "file::memory:?_pragma=foreign_keys(1)"
	}
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(1)
	if _, err := db.Exec(schema); err != nil {
		db.Close()
		return nil, err
	}
	return &Store{db: db}, nil
}

func (s *Store) Close() error { return s.db.Close() }

func randomHex(n int) string {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}

func ts(t time.Time) int64 {
	if t.IsZero() {
		return 0
	}
	return t.UnixNano()
}

func fromTS(n int64) time.Time {
	if n == 0 {
		return time.Time{}
	}
	return time.Unix(0, n).UTC()
}
