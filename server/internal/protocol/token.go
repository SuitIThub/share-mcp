package protocol

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"strings"
)

func tokenMAC(secret []byte, transferID, deviceID string) []byte {
	m := hmac.New(sha256.New, secret)
	m.Write([]byte(transferID + "|" + deviceID))
	return m.Sum(nil)
}

// AccessToken returns the token a receiver presents to the sender's local endpoint.
func AccessToken(secret []byte, transferID, deviceID string) string {
	return deviceID + "." + hex.EncodeToString(tokenMAC(secret, transferID, deviceID))
}

// VerifyAccessToken checks a token and returns the receiver device ID.
func VerifyAccessToken(secret []byte, transferID, token string) (string, bool) {
	i := strings.LastIndexByte(token, '.')
	if i <= 0 {
		return "", false
	}
	deviceID := token[:i]
	got, err := hex.DecodeString(token[i+1:])
	if err != nil {
		return "", false
	}
	if !hmac.Equal(got, tokenMAC(secret, transferID, deviceID)) {
		return "", false
	}
	return deviceID, true
}
