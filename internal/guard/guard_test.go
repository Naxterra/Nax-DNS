package guard

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

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

// An upgrade or rollback may stop the resolver in strict mode only while the
// guard really blocks: a running guard whose WinDivert filter failed to open
// must not count as ready, and neither may a report from before its start.
// A filter worded differently by an older guard still counts, but only if it
// blocks everything the configuration needs.
func TestGuardCountsAsBlockingOnlyWithAFilterOpen(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("NAXDNS_DATA", dir)
	path := filepath.Join(dir, "config.json")
	cfg := config.Default()
	cfg.Active = true // strict mode is the default failure mode
	if err := cfg.Save(path); err != nil {
		t.Fatal(err)
	}
	since := time.Now().Add(-time.Second)
	cases := []struct {
		name string
		st   Status
		ok   bool
	}{
		{"filter failed to open", Status{Enabled: false, Error: "access denied"}, false},
		{"older guard blocking UDP only", Status{Enabled: true, Filter: "outbound and udp.DstPort == 53"}, false},
		{"older guard skipping loopback", Status{Enabled: true, Filter: "outbound and !loopback and (udp.DstPort == 53 or tcp.DstPort == 53)"}, false},
		{"older guard with UDP and TCP", Status{Enabled: true, Filter: "outbound and ((udp.DstPort == 53 and (udp.SrcPort < 48600 or udp.SrcPort > 48999)) or tcp.DstPort == 53)"}, true},
		{"current filter", Status{Enabled: true, Filter: Filter(cfg.Settings)}, true},
	}
	for _, c := range cases {
		writeStatus(c.st)
		if got := Blocking(since); got != c.ok {
			t.Errorf("%s: Blocking=%v, want %v", c.name, got, c.ok)
		}
	}
	if Blocking(time.Now().Add(time.Minute)) {
		t.Error("a report from before the guard was started counted as ready")
	}
	writeStatus(Status{Stopped: true})
	if Blocking(since) {
		t.Error("a stopped guard counted as blocking in strict mode")
	}
}

// With protection off, or outside strict mode, nothing has to be blocked, so
// an upgrade must go ahead whatever state the guard is in.
func TestUpgradeIsAllowedWithoutStrictModeWhateverTheGuardState(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("NAXDNS_DATA", dir)
	cfg := config.Default()
	cfg.Active = false
	if err := cfg.Save(filepath.Join(dir, "config.json")); err != nil {
		t.Fatal(err)
	}
	writeStatus(Status{Stopped: true})
	if !Blocking(time.Now()) {
		t.Error("with protection off, a stopped guard blocked the upgrade")
	}
}

// While the resolver restores a missing config.json from its working copy,
// Load would read the defaults (protection off). That must not let a guard
// that does not block pass as safe.
func TestMissingConfigurationWithWorkingCopyCountsAsStrict(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("NAXDNS_DATA", dir)
	path := filepath.Join(dir, "config.json")
	cfg := config.Default()
	cfg.Active = true
	if err := cfg.Save(path); err != nil {
		t.Fatal(err)
	}
	if err := cfg.Save(path); err != nil { // the second save leaves a working copy
		t.Fatal(err)
	}
	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(config.BackupPath(path)); err != nil {
		t.Fatalf("no working copy to test with: %v", err)
	}
	writeStatus(Status{Enabled: false})
	if Blocking(time.Now().Add(-time.Second)) {
		t.Error("a guard that does not block passed as safe while config.json was being restored")
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
