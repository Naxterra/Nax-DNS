package guard

import (
	"strings"
	"testing"

	"naxdns/internal/config"
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
	for _, want := range []string{"outbound", "udp.DstPort == 53", "tcp.DstPort == 53", "udp.SrcPort < 48600", "udp.SrcPort > 48699"} {
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
