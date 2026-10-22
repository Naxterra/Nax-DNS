// Package guard is the strict-mode kill switch. It runs as its own Windows
// service, separate from the resolver, and holds a WinDivert handle of the
// lowest priority that drops outbound DNS on port 53.
//
// While the resolver runs, its handle has the highest priority and takes
// every query first, so the guard only sees packets that the resolver
// releases. If the resolver crashes, stops or is restarting its handles, the
// guard is the only handle left and DNS fails instead of leaking to the
// servers configured in Windows or a VPN.
package guard

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"time"

	"naxdns/internal/config"
	"naxdns/internal/divert"
	"naxdns/internal/ownports"
)

// ServiceName is the Windows service that runs the guard.
const ServiceName = "NaxDNSGuard"

const (
	statusFile   = "guard.json"
	pollInterval = time.Second
)

// Enabled reports whether cfg asks for the kill switch. It is on while
// protection is active in strict mode, which never releases a query to the
// original DNS server.
func Enabled(cfg *config.Config) bool {
	return cfg.Active && cfg.Settings.OnFailure == config.FailServfail
}

// Filter returns the WinDivert filter for the packets the guard drops. It
// covers what the resolver intercepts, minus the reserved ports from which the
// resolver itself sends plain DNS.
func Filter(s config.Settings) string {
	udp := fmt.Sprintf("(udp.DstPort == 53 and (udp.SrcPort < %d or udp.SrcPort > %d))", ownports.Low, ownports.High)
	match := udp
	if s.TCPMode != config.TCPAllow {
		match = "(" + udp + " or tcp.DstPort == 53)"
	}
	filter := "outbound and " + match
	if !s.InterceptLoopback {
		filter = "outbound and !loopback and " + match
	}
	return filter
}

// Status is written by the guard and shown by the resolver service.
type Status struct {
	Enabled bool      `json:"enabled"`
	Filter  string    `json:"filter,omitempty"`
	Error   string    `json:"error,omitempty"`
	Updated time.Time `json:"updated"`
}

// ReadStatus returns the guard's last report. A report older than a few poll
// intervals means the guard is not running.
func ReadStatus() (Status, bool) {
	var st Status
	data, err := os.ReadFile(filepath.Join(config.Dir(), statusFile))
	if err != nil || json.Unmarshal(data, &st) != nil {
		return st, false
	}
	return st, time.Since(st.Updated) < 5*pollInterval
}

func writeStatus(st Status) {
	st.Updated = time.Now()
	data, _ := json.Marshal(st)
	path := filepath.Join(config.Dir(), statusFile)
	if os.WriteFile(path+".tmp", data, 0o644) == nil {
		os.Rename(path+".tmp", path)
	}
}

// Run follows the saved configuration until ctx ends. An unreadable
// configuration leaves the current state unchanged, so a damaged file never
// opens the kill switch by itself.
func Run(ctx context.Context) {
	var handle *divert.Handle
	current, lastErr := "", ""
	os.MkdirAll(config.Dir(), 0o755)
	defer func() {
		if handle != nil {
			handle.Shutdown()
			handle.Close()
		}
		writeStatus(Status{})
	}()
	path := filepath.Join(config.Dir(), "config.json")
	tick := time.NewTicker(pollInterval)
	defer tick.Stop()
	for {
		want := current
		if cfg, err := config.Load(path); err == nil {
			want = ""
			if Enabled(cfg) {
				want = Filter(cfg.Settings)
			}
		}
		if want != current {
			if handle != nil {
				handle.Shutdown()
				handle.Close()
				handle = nil
			}
			current = ""
			if want != "" {
				h, err := divert.Open(want, -divert.PriorityHighest, divert.FlagDrop)
				if err != nil {
					// Opening is retried every poll; the error is logged once.
					if err.Error() != lastErr {
						log.Printf("guard: %v", err)
					}
					lastErr = err.Error()
				} else {
					lastErr = ""
					handle, current = h, want
					log.Printf("guard: blocking DNS that the resolver does not handle (%s)", want)
				}
			} else {
				lastErr = ""
				log.Printf("guard: off")
			}
		}
		writeStatus(Status{Enabled: handle != nil, Filter: current, Error: lastErr})
		select {
		case <-ctx.Done():
			return
		case <-tick.C:
		}
	}
}
