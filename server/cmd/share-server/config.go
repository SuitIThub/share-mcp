package main

import (
	"errors"
	"net/url"
	"os"

	"github.com/BurntSushi/toml"
)

type fileConfig struct {
	Listen              string `toml:"listen"`
	DBPath              string `toml:"db_path"`
	PublicURL           string `toml:"public_url"`
	MaxOfferMinutes     int    `toml:"max_offer_minutes"`
	FirebaseCredentials string `toml:"firebase_credentials"`
}

// loadConfig reads the TOML file; a missing file yields defaults.
func loadConfig(path string) (fileConfig, error) {
	c := fileConfig{Listen: "127.0.0.1:8080", DBPath: "/var/lib/share-server/share.db", MaxOfferMinutes: 10}
	if _, err := toml.DecodeFile(path, &c); err != nil && !errors.Is(err, os.ErrNotExist) {
		return c, err
	}
	if c.MaxOfferMinutes <= 0 || c.MaxOfferMinutes > 10 {
		c.MaxOfferMinutes = 10
	}
	return c, nil
}

// pairURI is the content of the pairing QR code.
func pairURI(serverURL, code string) string {
	q := url.Values{"url": {serverURL}, "code": {code}}
	return "share://pair?" + q.Encode()
}
