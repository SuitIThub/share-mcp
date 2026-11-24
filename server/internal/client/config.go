// Package client is the Go reference implementation of the share protocol (sender and receiver).
package client

import (
	"encoding/json"
	"os"
	"path/filepath"
)

type Config struct {
	ServerURL string `json:"server_url"`
	DeviceID  string `json:"device_id"`
	Token     string `json:"token"`
	CertPEM   []byte `json:"cert_pem"`
	KeyPEM    []byte `json:"key_pem"`
}

func DefaultConfigPath() string {
	dir, err := os.UserConfigDir()
	if err != nil {
		dir = "."
	}
	return filepath.Join(dir, "share", "config.json")
}

func LoadConfig(path string) (Config, error) {
	var c Config
	b, err := os.ReadFile(path)
	if err != nil {
		return c, err
	}
	return c, json.Unmarshal(b, &c)
}

func (c Config) Save(path string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return err
	}
	b, _ := json.MarshalIndent(c, "", "  ")
	return os.WriteFile(path, b, 0600)
}
