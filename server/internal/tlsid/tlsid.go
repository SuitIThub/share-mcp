// Package tlsid manages a device's self-signed TLS identity and fingerprint pinning.
package tlsid

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/pem"
	"errors"
	"math/big"
	"time"
)

// Generate creates a self-signed ECDSA P-256 certificate valid for 20 years.
func Generate(name string) (certPEM, keyPEM []byte, err error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, nil, err
	}
	serial, _ := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 120))
	tpl := &x509.Certificate{
		SerialNumber: serial,
		Subject:      pkix.Name{CommonName: "share " + name},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().AddDate(20, 0, 0),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, tpl, tpl, &key.PublicKey, key)
	if err != nil {
		return nil, nil, err
	}
	kder, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		return nil, nil, err
	}
	return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: kder}), nil
}

// Fingerprint is the lowercase hex SHA-256 of the DER certificate.
func Fingerprint(certDER []byte) string {
	h := sha256.Sum256(certDER)
	return hex.EncodeToString(h[:])
}

// FingerprintPEM computes the fingerprint of a PEM certificate.
func FingerprintPEM(certPEM []byte) (string, error) {
	b, _ := pem.Decode(certPEM)
	if b == nil || b.Type != "CERTIFICATE" {
		return "", errors.New("no certificate in PEM")
	}
	return Fingerprint(b.Bytes), nil
}

func ServerConfig(certPEM, keyPEM []byte) (*tls.Config, error) {
	c, err := tls.X509KeyPair(certPEM, keyPEM)
	if err != nil {
		return nil, err
	}
	return &tls.Config{Certificates: []tls.Certificate{c}, MinVersion: tls.VersionTLS12}, nil
}

// PinnedClientConfig accepts only a peer whose leaf certificate has the given fingerprint.
func PinnedClientConfig(fingerprint string) *tls.Config {
	return &tls.Config{
		InsecureSkipVerify: true, // replaced by fingerprint pinning below
		MinVersion:         tls.VersionTLS12,
		VerifyPeerCertificate: func(raw [][]byte, _ [][]*x509.Certificate) error {
			if len(raw) == 0 || Fingerprint(raw[0]) != fingerprint {
				return errors.New("certificate fingerprint mismatch")
			}
			return nil
		},
	}
}
