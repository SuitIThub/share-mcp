// Package mcpserver exposes share as MCP tools for AI agents.
package mcpserver

import (
	"context"
	"errors"
	"fmt"
	"path/filepath"
	"sync"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"share/internal/client"
	"share/internal/protocol"
)

const note = " The receiving device must confirm. The offer stays open for at most 10 minutes and only while this MCP session runs."

type DeviceInfo struct {
	Name     string    `json:"name"`
	Type     string    `json:"type"`
	Online   bool      `json:"online"`
	LastSeen time.Time `json:"last_seen"`
}

type ListDevicesOutput struct {
	Devices []DeviceInfo `json:"devices"`
}

type TargetArgs struct {
	Targets []string `json:"targets,omitempty" jsonschema:"device names (case-insensitive, unique prefix allowed); use either targets or all"`
	All     bool     `json:"all,omitempty" jsonschema:"offer to all devices; every device that accepts within the offer window receives it"`
}

type SendFilesArgs struct {
	Paths []string `json:"paths" jsonschema:"local file paths; relative paths are resolved against the working directory"`
	TargetArgs
	Message string `json:"message,omitempty" jsonschema:"optional note shown to the receiver"`
	Minutes int    `json:"minutes,omitempty" jsonschema:"offer duration in minutes, 1-10 (default 10)"`
}

type SendTextArgs struct {
	Text string `json:"text"`
	TargetArgs
}

type SendLinkArgs struct {
	URL string `json:"url"`
	TargetArgs
}

type SendOutput struct {
	TransferID string    `json:"transfer_id"`
	ExpiresAt  time.Time `json:"expires_at"`
	Recipients []string  `json:"recipients"`
}

type StatusArgs struct {
	TransferID  string `json:"transfer_id"`
	WaitSeconds int    `json:"wait_seconds,omitempty" jsonschema:"wait up to this many seconds (max 120) for the next status change"`
}

type DeliveryInfo struct {
	Device  string `json:"device"`
	Status  string `json:"status"`
	Path    string `json:"path,omitempty"`
	Percent int    `json:"percent"`
}

type StatusOutput struct {
	Closed     bool           `json:"closed"`
	ExpiresAt  time.Time      `json:"expires_at"`
	Deliveries []DeliveryInfo `json:"deliveries"`
}

type CancelArgs struct {
	TransferID string `json:"transfer_id"`
}

type CancelOutput struct {
	Cancelled bool `json:"cancelled"`
}

type tools struct {
	api    *client.API
	sender *client.Sender

	mu      sync.Mutex
	waiters map[string][]chan struct{}
}

// New creates the MCP server. It consumes sender.Updates().
func New(api *client.API, sender *client.Sender) *mcp.Server {
	t := &tools{api: api, sender: sender, waiters: map[string][]chan struct{}{}}
	go t.pump()
	s := mcp.NewServer(&mcp.Implementation{Name: "share", Version: "1.0.0"}, nil)
	mcp.AddTool(s, &mcp.Tool{Name: "list_devices", Description: "List the user's devices that can receive files, text and links."}, t.listDevices)
	mcp.AddTool(s, &mcp.Tool{Name: "send_files", Description: "Offer local files (e.g. a built APK) to specific devices or to all devices. Returns immediately with a transfer_id; use get_transfer_status to follow it." + note}, t.sendFiles)
	mcp.AddTool(s, &mcp.Tool{Name: "send_text", Description: "Send a text snippet (max 64 KB) to devices; on accept it lands in the device clipboard." + note}, t.sendText)
	mcp.AddTool(s, &mcp.Tool{Name: "send_link", Description: "Send a web link to devices; on accept it can be opened there." + note}, t.sendLink)
	mcp.AddTool(s, &mcp.Tool{Name: "get_transfer_status", Description: "Status per receiving device (waiting, accepted, downloading, delivered, declined, missed, cancelled, failed). Optionally waits for the next change."}, t.status)
	mcp.AddTool(s, &mcp.Tool{Name: "cancel_transfer", Description: "Withdraw an open offer."}, t.cancel)
	return s
}

func (t *tools) pump() {
	for u := range t.sender.Updates() {
		t.mu.Lock()
		for _, w := range t.waiters[u.TransferID] {
			close(w)
		}
		delete(t.waiters, u.TransferID)
		t.mu.Unlock()
	}
}

func (t *tools) wait(id string) <-chan struct{} {
	ch := make(chan struct{})
	t.mu.Lock()
	t.waiters[id] = append(t.waiters[id], ch)
	t.mu.Unlock()
	return ch
}

func (t *tools) listDevices(ctx context.Context, _ *mcp.CallToolRequest, _ struct{}) (*mcp.CallToolResult, ListDevicesOutput, error) {
	devs, err := t.api.Devices(ctx)
	if err != nil {
		return nil, ListDevicesOutput{}, err
	}
	out := ListDevicesOutput{Devices: []DeviceInfo{}}
	for _, d := range devs {
		if d.Type != protocol.DeviceAgent {
			out.Devices = append(out.Devices, DeviceInfo{Name: d.Name, Type: string(d.Type), Online: d.Online, LastSeen: d.LastSeen})
		}
	}
	return nil, out, nil
}

// resolve validates the target mode and returns target IDs plus a name lookup.
func (t *tools) resolve(ctx context.Context, a TargetArgs) ([]string, map[string]string, error) {
	if (len(a.Targets) > 0) == a.All {
		return nil, nil, errors.New("specify exactly one of 'targets' or 'all'")
	}
	devs, err := t.api.Devices(ctx)
	if err != nil {
		return nil, nil, err
	}
	names := map[string]string{}
	for _, d := range devs {
		names[d.ID] = d.Name
	}
	if a.All {
		return nil, names, nil
	}
	ids, err := client.ResolveTargets(devs, a.Targets)
	return ids, names, err
}

func sendOutput(tr protocol.Transfer, names map[string]string) SendOutput {
	out := SendOutput{TransferID: tr.ID, ExpiresAt: tr.ExpiresAt, Recipients: []string{}}
	for _, d := range tr.Deliveries {
		out.Recipients = append(out.Recipients, names[d.DeviceID])
	}
	return out
}

func (t *tools) sendFiles(ctx context.Context, _ *mcp.CallToolRequest, a SendFilesArgs) (*mcp.CallToolResult, SendOutput, error) {
	if len(a.Paths) == 0 {
		return nil, SendOutput{}, errors.New("no paths given")
	}
	ids, names, err := t.resolve(ctx, a.TargetArgs)
	if err != nil {
		return nil, SendOutput{}, err
	}
	paths := make([]string, len(a.Paths))
	for i, p := range a.Paths {
		if paths[i], err = filepath.Abs(p); err != nil {
			return nil, SendOutput{}, err
		}
	}
	if err := t.sender.WaitReady(ctx, 10*time.Second); err != nil {
		return nil, SendOutput{}, err
	}
	tr, err := t.sender.OfferFiles(ctx, paths, ids, a.All, a.Message, a.Minutes)
	if err != nil {
		return nil, SendOutput{}, err
	}
	return nil, sendOutput(tr, names), nil
}

func (t *tools) sendKind(ctx context.Context, kind protocol.Kind, text string, a TargetArgs) (*mcp.CallToolResult, SendOutput, error) {
	ids, names, err := t.resolve(ctx, a)
	if err != nil {
		return nil, SendOutput{}, err
	}
	tr, err := t.sender.OfferText(ctx, kind, text, ids, a.All)
	if err != nil {
		return nil, SendOutput{}, err
	}
	return nil, sendOutput(tr, names), nil
}

func (t *tools) sendText(ctx context.Context, _ *mcp.CallToolRequest, a SendTextArgs) (*mcp.CallToolResult, SendOutput, error) {
	return t.sendKind(ctx, protocol.KindText, a.Text, a.TargetArgs)
}

func (t *tools) sendLink(ctx context.Context, _ *mcp.CallToolRequest, a SendLinkArgs) (*mcp.CallToolResult, SendOutput, error) {
	return t.sendKind(ctx, protocol.KindLink, a.URL, a.TargetArgs)
}

func (t *tools) status(ctx context.Context, _ *mcp.CallToolRequest, a StatusArgs) (*mcp.CallToolResult, StatusOutput, error) {
	if a.WaitSeconds > 120 {
		a.WaitSeconds = 120
	}
	var changed <-chan struct{}
	if a.WaitSeconds > 0 {
		changed = t.wait(a.TransferID) // register before reading to not miss a change
	}
	tr, err := t.api.Transfer(ctx, a.TransferID)
	if err != nil {
		return nil, StatusOutput{}, fmt.Errorf("transfer %s: %w", a.TransferID, err)
	}
	if a.WaitSeconds > 0 && !tr.Closed {
		select {
		case <-changed:
			if tr, err = t.api.Transfer(ctx, a.TransferID); err != nil {
				return nil, StatusOutput{}, err
			}
		case <-time.After(time.Duration(a.WaitSeconds) * time.Second):
		case <-ctx.Done():
			return nil, StatusOutput{}, ctx.Err()
		}
	}
	devs, _ := t.api.Devices(ctx)
	names := map[string]string{}
	for _, d := range devs {
		names[d.ID] = d.Name
	}
	out := StatusOutput{Closed: tr.Closed, ExpiresAt: tr.ExpiresAt, Deliveries: []DeliveryInfo{}}
	total := tr.TotalSize()
	for _, d := range tr.Deliveries {
		pct := 0
		switch {
		case d.Status == protocol.StatusDelivered:
			pct = 100
		case total > 0:
			pct = int(d.Bytes * 100 / total)
		}
		out.Deliveries = append(out.Deliveries, DeliveryInfo{Device: names[d.DeviceID], Status: string(d.Status), Path: string(d.Path), Percent: pct})
	}
	return nil, out, nil
}

func (t *tools) cancel(ctx context.Context, _ *mcp.CallToolRequest, a CancelArgs) (*mcp.CallToolResult, CancelOutput, error) {
	if err := t.sender.Cancel(ctx, a.TransferID); err != nil {
		return nil, CancelOutput{}, err
	}
	return nil, CancelOutput{Cancelled: true}, nil
}
