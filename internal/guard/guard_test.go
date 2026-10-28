package guard

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"golang.org/x/sys/windows"

	"naxdns/internal/config"
	"naxdns/internal/ownports"
)

func TestEnabledOnlyForActiveStrictMode(t *testing.T) {
	cases := []struct {
		active    bool
		onFailure string
		want      bool
	}{
		{true, config.FailServfail, true},
		{true, config.FailPassthrough, false},
		{false, config.FailServfail, false},
	}
	for _, c := range cases {
		cfg := config.Default()
		cfg.Active, cfg.Settings.OnFailure = c.active, c.onFailure
		if got := Enabled(cfg); got != c.want {
			t.Errorf("active=%v onFailure=%s: Enabled=%v, want %v", c.active, c.onFailure, got, c.want)
		}
	}
}

func TestFilterMatchesWhatTheResolverIntercepts(t *testing.T) {
	s := config.Default().Settings
	f := Filter(s)
	for _, want := range []string{"outbound", "udp.DstPort == 53", "tcp.DstPort == 53", fmt.Sprintf("udp.SrcPort < %d", ownports.Low), fmt.Sprintf("udp.SrcPort > %d", ownports.High)} {
		if !strings.Contains(f, want) {
			t.Errorf("filter %q lacks %q", f, want)
		}
	}
	if strings.Contains(f, "loopback") {
		t.Errorf("loopback DNS is intercepted by default, so the guard must cover it too: %q", f)
	}

	s.InterceptLoopback = false
	if !strings.Contains(Filter(s), "!loopback") {
		t.Error("local resolvers must stay reachable when they are not intercepted")
	}
	s.TCPMode = config.TCPAllow
	if strings.Contains(Filter(s), "tcp") {
		t.Error("TCP must not be blocked when it is explicitly allowed")
	}
}

func TestStoppedGuardIsNotReportedAsRunning(t *testing.T) {
	t.Setenv("NAXDNS_DATA", t.TempDir())
	writeStatus(Status{Enabled: true})
	if _, running := ReadStatus(); !running {
		t.Fatal("a fresh report of a running guard was not recognised")
	}
	writeStatus(Status{Stopped: true})
	if _, running := ReadStatus(); running {
		t.Error("a guard that just stopped is reported as running")
	}
}

// The guard must keep blocking when it starts while config.json is damaged or
// locked, instead of reading that as protection off.
func TestUnreadableConfigurationKeepsTheRememberedFilter(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	remembered := Filter(config.Default().Settings)
	if err := os.WriteFile(path, []byte("{not json"), 0o644); err != nil {
		t.Fatal(err)
	}
	if got := wanted(path, remembered); got != remembered {
		t.Errorf("a damaged configuration changed the guard to %q", got)
	}

	cfg := config.Default()
	cfg.Active = true
	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	if err := cfg.Save(path); err != nil {
		t.Fatal(err)
	}
	p, _ := windows.UTF16PtrFromString(path)
	h, err := windows.CreateFile(p, windows.GENERIC_READ, 0, nil, windows.OPEN_EXISTING, windows.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		t.Fatal(err)
	}
	if got := wanted(path, remembered); got != remembered {
		t.Errorf("a locked configuration changed the guard to %q", got)
	}
	windows.CloseHandle(h)

	cfg.Active = false
	if err := cfg.Save(path); err != nil {
		t.Fatal(err)
	}
	if got := wanted(path, remembered); got != "" {
		t.Errorf("a readable configuration with protection off left the guard at %q", got)
	}
}
