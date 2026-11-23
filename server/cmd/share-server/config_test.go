package main

import (
	"os"
	"path/filepath"
	"testing"
)

func writeTemp(t *testing.T, content string) string {
	p := filepath.Join(t.TempDir(), "config.toml")
	os.WriteFile(p, []byte(content), 0600)
	return p
}

func TestConfigDefaultsAndClamp(t *testing.T) {
	c, err := loadConfig(writeTemp(t, ""))
	if err != nil {
		t.Fatal(err)
	}
	if c.Listen != "127.0.0.1:8080" || c.DBPath != "/var/lib/share-server/share.db" || c.MaxOfferMinutes != 10 || c.FirebaseCredentials != "" {
		t.Fatal(c)
	}
	c, _ = loadConfig(writeTemp(t, "max_offer_minutes = 30\nlisten = \":9000\"\npublic_url = \"https://x.example\"\n"))
	if c.MaxOfferMinutes != 10 || c.Listen != ":9000" || c.PublicURL != "https://x.example" {
		t.Fatal(c)
	}
	c, _ = loadConfig(writeTemp(t, "max_offer_minutes = 5\n"))
	if c.MaxOfferMinutes != 5 {
		t.Fatal(c)
	}
	if _, err := loadConfig(writeTemp(t, "listen = [")); err == nil {
		t.Fatal("bad toml accepted")
	}
}

func TestMissingConfigFileUsesDefaults(t *testing.T) {
	c, err := loadConfig(filepath.Join(t.TempDir(), "nope.toml"))
	if err != nil || c.Listen != "127.0.0.1:8080" {
		t.Fatal(c, err)
	}
}

func TestPairURI(t *testing.T) {
	if u := pairURI("https://a.b/x", "ABC234"); u != "share://pair?code=ABC234&url=https%3A%2F%2Fa.b%2Fx" {
		t.Fatal(u)
	}
}
