package client

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"share/internal/protocol"
)

// ResolveTargets maps user-given names to device IDs: exact (case-insensitive) first,
// then unique prefix. Agents are never targets. Duplicates are removed, order kept.
func ResolveTargets(devs []protocol.Device, names []string) ([]string, error) {
	var out []string
	seen := map[string]bool{}
	for _, n := range names {
		q := strings.ToLower(strings.TrimSpace(n))
		if q == "" {
			return nil, fmt.Errorf("empty device name")
		}
		var exact, prefix []protocol.Device
		for _, d := range devs {
			if d.Type == protocol.DeviceAgent {
				continue
			}
			l := strings.ToLower(d.Name)
			if l == q {
				exact = append(exact, d)
			} else if strings.HasPrefix(l, q) {
				prefix = append(prefix, d)
			}
		}
		var id string
		switch {
		case len(exact) == 1:
			id = exact[0].ID
		case len(prefix) == 1:
			id = prefix[0].ID
		case len(prefix) > 1:
			var ns []string
			for _, d := range prefix {
				ns = append(ns, d.Name)
			}
			return nil, fmt.Errorf("device name %q is ambiguous: %s", n, strings.Join(ns, ", "))
		default:
			return nil, fmt.Errorf("unknown device %q", n)
		}
		if !seen[id] {
			seen[id] = true
			out = append(out, id)
		}
	}
	return out, nil
}

var reserved = map[string]bool{"CON": true, "PRN": true, "AUX": true, "NUL": true}

func init() {
	for i := 1; i <= 9; i++ {
		reserved[fmt.Sprintf("COM%d", i)] = true
		reserved[fmt.Sprintf("LPT%d", i)] = true
	}
}

const badChars = "<>:\"|?*"

// SafeFileName reduces a sender-supplied name to a safe base name for Windows and Android.
func SafeFileName(name string) string {
	if i := strings.LastIndexAny(name, "/\\"); i >= 0 {
		name = name[i+1:]
	}
	var b strings.Builder
	for _, r := range name {
		if r < 32 || strings.ContainsRune(badChars, r) {
			b.WriteRune('_')
		} else {
			b.WriteRune(r)
		}
	}
	name = strings.TrimSpace(b.String())
	name = strings.TrimRight(name, ". ")
	if name == "" || name == "." || name == ".." {
		return "file"
	}
	base := name
	if i := strings.IndexByte(base, '.'); i >= 0 {
		base = base[:i]
	}
	if reserved[strings.ToUpper(base)] {
		name = "_" + name
	}
	if len(name) > 200 {
		ext := filepath.Ext(name)
		if len(ext) > 20 {
			ext = ""
		}
		name = name[:200-len(ext)] + ext
	}
	return name
}

// UniquePath returns dir/name, or dir/"name (n).ext" if it exists.
func UniquePath(dir, name string) string {
	p := filepath.Join(dir, name)
	if _, err := os.Lstat(p); os.IsNotExist(err) {
		return p
	}
	ext := filepath.Ext(name)
	stem := strings.TrimSuffix(name, ext)
	if stem == "" { // e.g. ".hidden"
		stem, ext = name, ""
	}
	for i := 1; ; i++ {
		p = filepath.Join(dir, fmt.Sprintf("%s (%d)%s", stem, i, ext))
		if _, err := os.Lstat(p); os.IsNotExist(err) {
			return p
		}
	}
}
