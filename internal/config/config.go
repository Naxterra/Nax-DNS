// Package config holds the persisted Nax-DNSManager configuration.
package config

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/netip"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// Rule actions.
const (
	ActionBlock       = "block"
	ActionPassthrough = "passthrough"
	ActionUpstream    = "upstream"
)

// What to do when no encrypted server can answer.
const (
	FailPassthrough = "passthrough" // let the query reach the DNS server it was sent to
	FailServfail    = "servfail"    // strict: never leave the encrypted path
)

// TCP/53 handling.
const (
	TCPProxy = "proxy"
	TCPBlock = "block"
	TCPAllow = "allow"
)

// Upstream is one DNS server. URL schemes: https:// (DoH), h3:// (DoH3),
// tls:// (DoT), quic:// (DoQ), udp:// (plain, for local resolvers only).
type Upstream struct {
	ID        string   `json:"id"`
	Name      string   `json:"name"`
	URL       string   `json:"url"`
	Bootstrap []string `json:"bootstrap,omitempty"`
	Enabled   bool     `json:"enabled"`
}

// Rule routes matching domains. A pattern "example.com" matches the domain
// and its subdomains, "*.example.com" only subdomains, "<single-label>" any
// name without a dot.
type Rule struct {
	ID       string   `json:"id"`
	Name     string   `json:"name"`
	Enabled  bool     `json:"enabled"`
	Domains  []string `json:"domains"`
	Action   string   `json:"action"`
	Upstream string   `json:"upstream,omitempty"`
}

type Settings struct {
	TimeoutMs         int      `json:"timeoutMs"`
	HedgeMs           int      `json:"hedgeMs"`
	FailThreshold     int      `json:"failThreshold"`
	CooldownSec       int      `json:"cooldownSec"`
	OnFailure         string   `json:"onFailure"`
	Cache             bool     `json:"cache"`
	CacheMinTTL       int      `json:"cacheMinTtl"`
	CacheMaxTTL       int      `json:"cacheMaxTtl"`
	InterceptLoopback bool     `json:"interceptLoopback"`
	TCPMode           string   `json:"tcpMode"`
	BlockDDR          bool     `json:"blockDdr"`
	FirefoxCanary     bool     `json:"firefoxCanary"`
	BootstrapDoH      []string `json:"bootstrapDoh"`
}

// Config is the whole persisted state. Upstream order is failover priority.
type Config struct {
	// Version is the schema version. Files without it come from 0.1.x or
	// 0.2.x and are migrated when loaded.
	Version   int        `json:"version"`
	Active    bool       `json:"active"`
	Upstreams []Upstream `json:"upstreams"`
	Rules     []Rule     `json:"rules"`
	Settings  Settings   `json:"settings"`
}

// Default returns the first-run configuration: public privacy resolvers as a
// working chain until the user adds their NextDNS / Control D profiles.
func Default() *Config {
	return &Config{
		Version: SchemaVersion,
		Active:  false,
		Upstreams: []Upstream{
			{ID: "cloudflare", Name: "Cloudflare (DoH)", URL: "https://cloudflare-dns.com/dns-query", Enabled: true},
			{ID: "quad9", Name: "Quad9 (DoH)", URL: "https://dns.quad9.net/dns-query", Enabled: true},
		},
		Rules: []Rule{{
			// Answered locally (NXDOMAIN): these names mean nothing outside the
			// LAN, and forwarding them would hand them to whichever DNS server
			// Windows or a VPN configured. Switch the rule to "original DNS
			// server" to resolve router-provided names instead.
			ID: "local", Name: "Local names (answered locally)", Enabled: true, Action: ActionBlock,
			Domains: []string{
				"<single-label>", "lan", "local", "home.arpa", "internal", "localdomain", "fritz.box",
				"10.in-addr.arpa", "168.192.in-addr.arpa", "254.169.in-addr.arpa",
				"16.172.in-addr.arpa", "17.172.in-addr.arpa", "18.172.in-addr.arpa", "19.172.in-addr.arpa",
				"20.172.in-addr.arpa", "21.172.in-addr.arpa", "22.172.in-addr.arpa", "23.172.in-addr.arpa",
				"24.172.in-addr.arpa", "25.172.in-addr.arpa", "26.172.in-addr.arpa", "27.172.in-addr.arpa",
				"28.172.in-addr.arpa", "29.172.in-addr.arpa", "30.172.in-addr.arpa", "31.172.in-addr.arpa",
			},
		}},
		Settings: Settings{
			TimeoutMs: 3000, HedgeMs: 0, FailThreshold: 3, CooldownSec: 15,
			OnFailure: FailServfail,
			Cache:     true, CacheMinTTL: 0, CacheMaxTTL: 3600,
			InterceptLoopback: true, TCPMode: TCPProxy,
			BlockDDR: true, FirefoxCanary: true,
			BootstrapDoH: []string{"https://9.9.9.9/dns-query", "https://1.1.1.1/dns-query", "https://[2620:fe::fe]/dns-query"},
		},
	}
}

// Normalize fills unset settings with defaults so older files keep working.
func (c *Config) Normalize() {
	d := Default().Settings
	s := &c.Settings
	if s.TimeoutMs <= 0 {
		s.TimeoutMs = d.TimeoutMs
	}
	if s.HedgeMs < 0 {
		s.HedgeMs = 0 // 0: never ask a backup in parallel
	}
	if s.FailThreshold <= 0 {
		s.FailThreshold = d.FailThreshold
	}
	if s.CooldownSec <= 0 {
		s.CooldownSec = d.CooldownSec
	}
	if s.OnFailure == "" {
		s.OnFailure = d.OnFailure
	}
	if s.CacheMaxTTL <= 0 {
		s.CacheMaxTTL = d.CacheMaxTTL
	}
	if s.TCPMode == "" {
		s.TCPMode = d.TCPMode
	}
	if len(s.BootstrapDoH) == 0 {
		s.BootstrapDoH = d.BootstrapDoH
	}
	if c.Upstreams == nil {
		c.Upstreams = []Upstream{}
	}
	if c.Rules == nil {
		c.Rules = []Rule{}
	}
}

type limit struct {
	name   string
	value  *int
	lo, hi int
}

// limits bounds the numeric settings. Validate rejects values outside them
// when a change comes in, and migrate clamps them in saved files.
func (s *Settings) limits() []limit {
	return []limit{
		{"timeoutMs", &s.TimeoutMs, 500, 15000},
		{"hedgeMs", &s.HedgeMs, 0, 10000},
		{"failThreshold", &s.FailThreshold, 1, 20},
		{"cooldownSec", &s.CooldownSec, 1, 3600},
		{"cacheMinTtl", &s.CacheMinTTL, 0, 86400},
		{"cacheMaxTtl", &s.CacheMaxTTL, 10, 86400},
	}
}

// migrate brings a file written by an older version within the rules that
// Validate enforces today, so an upgrade never rejects a configuration that
// the previous version accepted (which would switch protection off). Numbers
// are clamped to their bounds, and strict mode with TCP DNS allowed becomes
// strict mode with TCP DNS proxied, which keeps strict mode's promise.
func (c *Config) migrate() {
	s := &c.Settings
	if c.Version < 1 {
		// 0.1.x saved parallel backup queries (1500 ms) as its default. They
		// are off by default now, so a file from before is brought in line.
		s.HedgeMs = 0
	}
	c.Version = SchemaVersion
	for _, b := range s.limits() {
		*b.value = min(max(*b.value, b.lo), b.hi)
	}
	s.CacheMinTTL = min(s.CacheMinTTL, s.CacheMaxTTL)
	if s.OnFailure == FailServfail && s.TCPMode == TCPAllow {
		s.TCPMode = TCPProxy
	}
}

// SchemaVersion is the configuration format written by this version.
const SchemaVersion = 1

// ValidateUpstreamURL checks scheme and host of a server URL.
func ValidateUpstreamURL(raw string) error {
	u, err := url.Parse(raw)
	if err != nil {
		return err
	}
	switch u.Scheme {
	case "https", "h3", "tls", "quic":
	case "udp":
		if _, err := netip.ParseAddr(u.Hostname()); err != nil {
			return errors.New("plain DNS servers must be given as an IP address")
		}
	default:
		return fmt.Errorf("unsupported scheme %q (use https://, h3://, tls://, quic:// or udp://)", u.Scheme)
	}
	if u.Hostname() == "" {
		return errors.New("missing host name")
	}
	return nil
}

func (c *Config) Validate() error {
	ids := map[string]bool{}
	enabled := 0
	for _, u := range c.Upstreams {
		if u.ID == "" || ids[u.ID] {
			return fmt.Errorf("server %q: missing or duplicate id", u.Name)
		}
		ids[u.ID] = true
		if err := ValidateUpstreamURL(u.URL); err != nil {
			return fmt.Errorf("server %q: %w", u.Name, err)
		}
		for _, b := range u.Bootstrap {
			if _, err := netip.ParseAddr(b); err != nil {
				return fmt.Errorf("server %q: bootstrap %q is not an IP address", u.Name, b)
			}
		}
		if u.Enabled {
			enabled++
		}
	}
	if c.Active && enabled == 0 {
		return errors.New("enable at least one server before turning protection on")
	}
	for _, r := range c.Rules {
		switch r.Action {
		case ActionBlock, ActionPassthrough:
		case ActionUpstream:
			if !ids[r.Upstream] {
				return fmt.Errorf("rule %q: unknown server %q", r.Name, r.Upstream)
			}
		default:
			return fmt.Errorf("rule %q: unknown action %q", r.Name, r.Action)
		}
		for _, d := range r.Domains {
			if strings.TrimSpace(d) == "" {
				return fmt.Errorf("rule %q: empty domain pattern", r.Name)
			}
		}
	}
	s := c.Settings
	if s.OnFailure != FailPassthrough && s.OnFailure != FailServfail {
		return fmt.Errorf("unknown onFailure %q", s.OnFailure)
	}
	if s.TCPMode != TCPProxy && s.TCPMode != TCPBlock && s.TCPMode != TCPAllow {
		return fmt.Errorf("unknown tcpMode %q", s.TCPMode)
	}
	if s.OnFailure == FailServfail && s.TCPMode == TCPAllow {
		return errors.New("strict mode cannot let TCP DNS pass unencrypted; choose proxy or block for TCP DNS, or turn strict mode off")
	}
	for _, b := range s.limits() {
		if *b.value < b.lo || *b.value > b.hi {
			return fmt.Errorf("%s must be between %d and %d", b.name, b.lo, b.hi)
		}
	}
	if s.CacheMinTTL > s.CacheMaxTTL {
		return errors.New("cacheMinTtl must not be larger than cacheMaxTtl")
	}
	for _, b := range s.BootstrapDoH {
		u, err := url.Parse(b)
		if err != nil || u.Scheme != "https" {
			return fmt.Errorf("bootstrap resolver %q must be an https:// URL with an IP address host", b)
		}
		if _, err := netip.ParseAddr(u.Hostname()); err != nil {
			return fmt.Errorf("bootstrap resolver %q must use an IP address host", b)
		}
	}
	return nil
}

// Dir is the service's data directory. NAXDNS_DATA overrides it for development.
func Dir() string {
	if d := os.Getenv("NAXDNS_DATA"); d != "" {
		return d
	}
	pd := os.Getenv("ProgramData")
	if pd == "" {
		pd = `C:\ProgramData`
	}
	return filepath.Join(pd, "NaxDNS")
}

// Load reads path, or returns defaults if it does not exist.
func Load(path string) (*Config, error) {
	data, err := readShared(path)
	if errors.Is(err, os.ErrNotExist) {
		return Default(), nil
	}
	if err != nil {
		return nil, err
	}
	c, err := parse(data)
	if err != nil {
		return c, fmt.Errorf("%s: %w", path, err)
	}
	return c, nil
}

// parse turns file content into a configuration that passes Validate.
func parse(data []byte) (*Config, error) {
	c := &Config{}
	if err := json.Unmarshal(data, c); err != nil {
		return nil, err
	}
	c.Normalize()
	c.migrate()
	return c, c.Validate()
}

// Recovered is the outcome of Recover.
type Recovered struct {
	Config *Config
	// Problem describes what happened; it is empty when the file was fine.
	Problem string
	// Save is true when config.json must be written from Config. It is false
	// when the file is fine, when it could only not be read for the moment,
	// so that a newer valid file is never replaced by an older copy, and when
	// a damaged file could not be kept.
	Save bool
	// Retry is true when config.json exists but could not be read. The caller
	// should load it again later and switch to it once it can be read.
	Retry bool
}

// Recover loads path. If the file is damaged, a copy of it is kept under a
// name ending in ".broken-" and the time, and the last working copy is used,
// so a damaged file does not silently switch protection off. A missing
// config.json next to a working copy counts as damaged too. A file that is
// only locked is retried and, if it stays unreadable, left alone. No
// damaged file is ever overwritten.
func Recover(path string) Recovered {
	cfg, err := Load(path)
	for i := 0; err != nil && !isDamage(err) && i < 30; i++ {
		time.Sleep(100 * time.Millisecond) // a scanner or another reader holds it
		cfg, err = Load(path)
	}
	backup := BackupPath(path)
	_, statErr := os.Stat(path)
	_, backupErr := os.Stat(backup)
	if err == nil && (statErr == nil || backupErr != nil) {
		return Recovered{Config: cfg}
	}
	if err == nil {
		err = errors.New("config.json is missing")
	}
	saved, prev := Default(), error(nil)
	if backupErr == nil {
		saved, prev = Load(backup)
	}
	if err != nil && !isDamage(err) {
		// Unreadable but not shown to be damaged: use the working copy for now
		// and leave the file as it is.
		if prev == nil && backupErr == nil {
			return Recovered{Config: saved, Retry: true, Problem: fmt.Sprintf("The configuration could not be read (%v). The last working configuration is in use until it can be read again.", err)}
		}
		return Recovered{Config: Default(), Retry: true, Problem: fmt.Sprintf("The configuration could not be read (%v). Default settings are in use and protection is off until it can be read again.", err)}
	}
	kept, ok := keepDamaged(path)
	if backupErr == nil && prev == nil {
		return Recovered{Config: saved, Save: ok, Problem: fmt.Sprintf("The configuration could not be read (%v). The last working configuration was restored. %s", err, kept)}
	}
	if backupErr == nil {
		keptCopy, okCopy := keepDamaged(backup)
		return Recovered{Config: Default(), Save: ok && okCopy, Problem: fmt.Sprintf("The configuration could not be read (%v), and its working copy is damaged too (%v). Default settings are in use and protection is off. %s %s", err, prev, kept, keptCopy)}
	}
	return Recovered{Config: Default(), Save: ok, Problem: fmt.Sprintf("The configuration could not be read (%v) and no working copy exists. Default settings are in use and protection is off. %s", err, kept)}
}

// isDamage reports whether a Load error means the content is unusable, as
// opposed to the file being missing, locked or otherwise unreadable.
func isDamage(err error) bool {
	var pathErr *os.PathError
	return !errors.As(err, &pathErr)
}

// keepDamaged copies a damaged file next to it under a name with the time,
// to the nanosecond, so no earlier copy is replaced, and says what happened.
// ok is false when a file exists but no copy could be made; it must then not
// be overwritten.
func keepDamaged(path string) (message string, ok bool) {
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return "", true // nothing to keep
	}
	if err != nil {
		return fmt.Sprintf("The damaged file %s could not be copied (%v), so it was left in place.", filepath.Base(path), err), false
	}
	name := path + ".broken-" + time.Now().Format("20060102-150405.000000000")
	if err := writeAtomic(name, data); err != nil {
		return fmt.Sprintf("The damaged file %s could not be copied (%v), so it was left in place.", filepath.Base(path), err), false
	}
	return fmt.Sprintf("The damaged file was kept as %s.", filepath.Base(name)), true
}

// BackupPath is the working copy that Save keeps next to path.
func BackupPath(path string) string { return path + ".last-good" }

// Save replaces the file atomically. The version it replaces, if it is valid,
// becomes the working copy for Recover, so the two files are never written
// with the same content at the same moment. If either write fails, Save
// returns an error before config.json has changed. A damaged file is copied
// aside like in Recover before it is replaced, and a file that exists but
// cannot be read is not replaced at all.
func (c *Config) Save(path string) error {
	data, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	previous, err := readShared(path)
	switch {
	case errors.Is(err, os.ErrNotExist):
		previous = data // the first save: keep the new version
	case err != nil:
		return fmt.Errorf("the current configuration could not be read, so it was not replaced: %w", err)
	default:
		if _, err := parse(previous); err != nil {
			if kept, ok := keepDamaged(path); !ok {
				return errors.New(kept)
			}
			previous = data
		}
	}
	if err := writeAtomic(BackupPath(path), previous); err != nil {
		return err
	}
	return writeAtomic(path, data)
}
