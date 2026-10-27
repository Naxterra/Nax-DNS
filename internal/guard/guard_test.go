package guard

import (
	"fmt"
	"strings"
	"testing"

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
