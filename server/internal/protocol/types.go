// Package protocol defines the wire contract shared by server and all clients.
package protocol

import (
	"encoding/json"
	"time"
)

type DeviceType string

const (
	DeviceAndroid DeviceType = "android"
	DeviceWindows DeviceType = "windows"
	DeviceAgent   DeviceType = "agent"
)

func (t DeviceType) Valid() bool {
	return t == DeviceAndroid || t == DeviceWindows || t == DeviceAgent
}

type Device struct {
	ID              string     `json:"id"`
	Name            string     `json:"name"`
	Type            DeviceType `json:"type"`
	Online          bool       `json:"online"`
	LastSeen        time.Time  `json:"last_seen"`
	CertFingerprint string     `json:"cert_fingerprint,omitempty"`
}

type Kind string

const (
	KindText  Kind = "text"
	KindLink  Kind = "link"
	KindFiles Kind = "files"
)

type FileItem struct {
	Name    string    `json:"name"`
	Size    int64     `json:"size"`
	MIME    string    `json:"mime"`
	ModTime time.Time `json:"mod_time"`
}

type Status string

const (
	StatusWaiting     Status = "waiting"
	StatusAccepted    Status = "accepted"
	StatusDownloading Status = "downloading"
	StatusDelivered   Status = "delivered"
	StatusDeclined    Status = "declined"
	StatusMissed      Status = "missed"
	StatusCancelled   Status = "cancelled"
	StatusFailed      Status = "failed"
)

// Final reports whether no further transition is possible.
func (s Status) Final() bool {
	switch s {
	case StatusDelivered, StatusDeclined, StatusMissed, StatusCancelled, StatusFailed:
		return true
	}
	return false
}

type Path string

const (
	PathLAN   Path = "lan"
	PathRelay Path = "relay"
)

type Delivery struct {
	DeviceID  string    `json:"device_id"`
	Status    Status    `json:"status"`
	Path      Path      `json:"path,omitempty"`
	Bytes     int64     `json:"bytes"`
	UpdatedAt time.Time `json:"updated_at"`
}

type Transfer struct {
	ID         string     `json:"id"`
	SenderID   string     `json:"sender_id"`
	Kind       Kind       `json:"kind"`
	Text       string     `json:"text,omitempty"`
	Message    string     `json:"message,omitempty"`
	Files      []FileItem `json:"files,omitempty"`
	Broadcast  bool       `json:"broadcast"`
	Targets    []string   `json:"targets,omitempty"`
	CreatedAt  time.Time  `json:"created_at"`
	ExpiresAt  time.Time  `json:"expires_at"`
	Closed     bool       `json:"closed"`
	Deliveries []Delivery `json:"deliveries,omitempty"`
}

// TotalSize is the sum of all file sizes.
func (t Transfer) TotalSize() int64 {
	var n int64
	for _, f := range t.Files {
		n += f.Size
	}
	return n
}

type CreateTransferRequest struct {
	Kind          Kind       `json:"kind"`
	Text          string     `json:"text,omitempty"`
	Message       string     `json:"message,omitempty"`
	Files         []FileItem `json:"files,omitempty"`
	Targets       []string   `json:"targets,omitempty"`
	Broadcast     bool       `json:"broadcast"`
	Minutes       int        `json:"minutes,omitempty"`
	LANCandidates []string   `json:"lan_candidates,omitempty"`
}

type CreateTransferResponse struct {
	Transfer Transfer `json:"transfer"`
	Secret   string   `json:"secret"` // hex, 32 bytes
}

type AcceptResponse struct {
	LANCandidates   []string `json:"lan_candidates"`
	CertFingerprint string   `json:"cert_fingerprint"`
	AccessToken     string   `json:"access_token"`
	RelayBase       string   `json:"relay_base"`
}

type PairRequest struct {
	Code            string     `json:"code"`
	Name            string     `json:"name"`
	Type            DeviceType `json:"type"`
	CertFingerprint string     `json:"cert_fingerprint"`
}

type PairResponse struct {
	DeviceID string `json:"device_id"`
	Token    string `json:"token"`
}

type PairCodeResponse struct {
	Code      string    `json:"code"`
	ExpiresAt time.Time `json:"expires_at"`
}

type UpdateDeviceRequest struct {
	Name     *string `json:"name,omitempty"`
	FCMToken *string `json:"fcm_token,omitempty"`
}

type ProgressRequest struct {
	Bytes int64 `json:"bytes"`
	Path  Path  `json:"path"`
}

type Event struct {
	Type string          `json:"type"`
	Data json.RawMessage `json:"data"`
}

const (
	EvOfferNew        = "offer.new"        // Data: Transfer
	EvOfferRemoved    = "offer.removed"    // Data: TransferRef
	EvDeliveryUpdated = "delivery.updated" // Data: DeliveryUpdate
	EvTransferClosed  = "transfer.closed"  // Data: TransferRef
	EvRelayRequest    = "relay.request"    // Data: RelayRequest
)

type TransferRef struct {
	TransferID string `json:"transfer_id"`
}

type DeliveryUpdate struct {
	TransferID string   `json:"transfer_id"`
	Delivery   Delivery `json:"delivery"`
}

type RelayRequest struct {
	RelayID    string `json:"relay_id"`
	TransferID string `json:"transfer_id"`
	FileIndex  int    `json:"file_index"`
	Offset     int64  `json:"offset"`
}

// NewEvent marshals data into an Event.
func NewEvent(typ string, data any) Event {
	b, _ := json.Marshal(data)
	return Event{Type: typ, Data: b}
}

// MaxTextBytes is the maximum size of a text/link payload.
const MaxTextBytes = 65536
