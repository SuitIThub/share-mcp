package client

import (
	"os"
	"path/filepath"
	"reflect"
	"testing"

	"share/internal/protocol"
)

func TestResolveTargets(t *testing.T) {
	devs := []protocol.Device{{ID: "1", Name: "Pixel 8", Type: "android"}, {ID: "2", Name: "Pixel Tablet", Type: "android"}, {ID: "3", Name: "Desktop", Type: "windows"}, {ID: "4", Name: "Agent", Type: "agent"}}
	ok := map[string][]string{"desktop": {"3"}, "pixel 8": {"1"}, "desk": {"3"}, "Pixel T": {"2"}}
	for in, want := range ok {
		got, err := ResolveTargets(devs, []string{in})
		if err != nil || !reflect.DeepEqual(got, want) {
			t.Errorf("%q: %v %v", in, got, err)
		}
	}
	for _, bad := range []string{"pixel", "agent", "foo", ""} {
		if _, err := ResolveTargets(devs, []string{bad}); err == nil {
			t.Errorf("%q accepted", bad)
		}
	}
	got, err := ResolveTargets(devs, []string{"desk", "Desktop", "pixel 8"})
	if err != nil || !reflect.DeepEqual(got, []string{"3", "1"}) {
		t.Fatal(got, err)
	}
}

// Review Focus 3
func TestSafeFileName(t *testing.T) {
	cases := map[string]string{
		"../evil.txt": "evil.txt", "a/b.txt": "b.txt", `a\b.txt`: "b.txt", "CON": "_CON", "con.txt": "_con.txt",
		"": "file", "x:y?.txt": "x_y_.txt", "  .hidden. ": ".hidden", "..": "file", "lpt1.log": "_lpt1.log", "app.apk": "app.apk",
	}
	for in, want := range cases {
		if got := SafeFileName(in); got != want {
			t.Errorf("SafeFileName(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestUniquePath(t *testing.T) {
	dir := t.TempDir()
	if p := UniquePath(dir, "a.txt"); p != filepath.Join(dir, "a.txt") {
		t.Fatal(p)
	}
	os.WriteFile(filepath.Join(dir, "a.txt"), nil, 0644)
	if p := UniquePath(dir, "a.txt"); p != filepath.Join(dir, "a (1).txt") {
		t.Fatal(p)
	}
	os.WriteFile(filepath.Join(dir, "a (1).txt"), nil, 0644)
	if p := UniquePath(dir, "a.txt"); p != filepath.Join(dir, "a (2).txt") {
		t.Fatal(p)
	}
	os.WriteFile(filepath.Join(dir, "noext"), nil, 0644)
	if p := UniquePath(dir, "noext"); p != filepath.Join(dir, "noext (1)") {
		t.Fatal(p)
	}
}
