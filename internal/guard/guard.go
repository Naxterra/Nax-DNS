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

	"golang.org/x/sys/windows/registry"

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
		tcp := fmt.Sprintf("(tcp.DstPort == 53 and (tcp.SrcPort < %d or tcp.SrcPort > %d))", ownports.Low, ownports.High)
		match = "(" + udp + " or " + tcp + ")"
	}
	filter := "outbound and " + match
	if !s.InterceptLoopback {
		filter = "outbound and !loopback and " + match
	}
	return filter
}

// Status is written by the guard and shown by the resolver service.
type Status struct {
	// Stopped is written when the guard exits, so it is never mistaken for a
	// running guard because its last report is still recent.
	Stopped bool      `json:"stopped,omitempty"`
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
	return st, !st.Stopped && time.Since(st.Updated) < 5*pollInterval
}

func writeStatus(st Status) {
	st.Updated = time.Now()
	data, _ := json.Marshal(st)
	path := filepath.Join(config.Dir(), statusFile)
	if os.WriteFile(path+".tmp", data, 0o644) == nil {
		os.Rename(path+".tmp", path)
	}
}

// stateKey keeps the guard's last filter where only SYSTEM and Administrators
// can write, so the guard can keep blocking even when the data folder cannot
// be trusted.
const stateKey = `SYSTEM\CurrentControlSet\Services\NaxDNSGuard\Parameters`

func rememberFilter(filter string) {
	k, _, err := registry.CreateKey(registry.LOCAL_MACHINE, stateKey, registry.SET_VALUE)
	if err != nil {
		log.Printf("guard: state not saved: %v", err)
		return
	}
	defer k.Close()
	k.SetStringValue("Filter", filter)
}

func rememberedFilter() string {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, stateKey, registry.QUERY_VALUE)
	if err != nil {
		return ""
	}
	defer k.Close()
	filter, _, _ := k.GetStringValue("Filter")
	return filter
}

// Run follows the saved configuration until ctx ends. An unreadable
// configuration leaves the current state unchanged, so a damaged file never
// opens the kill switch by itself. Each decision is also remembered in the
// registry. With trustedDir false the data folder could not be secured, so the
// guard ignores it and keeps the remembered state instead: in strict mode it
// keeps blocking although the resolver refuses to run. recheck, if given, is
// called every 30 seconds while the folder is untrusted; once it succeeds the
// guard follows the configuration again.
func Run(ctx context.Context, trustedDir bool, recheck func() error) {
	var handle *divert.Handle
	current, lastErr := "", ""
	report := func(st Status) {
		if trustedDir {
			writeStatus(st)
		}
	}
	defer func() {
		if handle != nil {
			handle.Shutdown()
			handle.Close()
		}
		report(Status{Stopped: true})
	}()
	path := filepath.Join(config.Dir(), "config.json")
	remembered := rememberedFilter()
	tick := time.NewTicker(pollInterval)
	defer tick.Stop()
	// The recheck runs in the background, so a stop request never waits for it.
	rechecked := make(chan error, 1)
	checking := false
	for polls := 1; ; polls++ {
		if !trustedDir && recheck != nil && !checking && polls%30 == 0 {
			checking = true
			go func() { rechecked <- recheck() }()
		}
		want := remembered
		if trustedDir {
			want = wanted(path, remembered)
			if want != remembered {
				rememberFilter(want)
				remembered = want
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
		report(Status{Enabled: handle != nil, Filter: current, Error: lastErr})
		select {
		case <-ctx.Done():
			return
		case err := <-rechecked:
			checking = false
			if err == nil {
				trustedDir = true
			}
		case <-tick.C:
		}
	}
}

// wanted returns the filter that the configuration at path asks for. Without a
// usable configuration it returns the remembered filter, which also covers a
// start while config.json is damaged or locked.
func wanted(path, remembered string) string {
	if missingWithCopy(path) {
		// The resolver is restoring config.json; Load would read the missing
		// file as a first start and switch the guard off.
		return remembered
	}
	cfg, err := config.Load(path)
	if err != nil {
		return remembered
	}
	if Enabled(cfg) {
		return Filter(cfg.Settings)
	}
	return ""
}

// missingWithCopy reports a config.json that is missing although a working
// copy exists, which happens only while the configuration is being restored.
func missingWithCopy(path string) bool {
	if _, err := os.Stat(path); err == nil {
		return false
	}
	_, err := os.Stat(config.BackupPath(path))
	return err == nil
}
