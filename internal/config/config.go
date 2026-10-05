// Package config holds the persisted NaxDNS configuration.
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
	Active    bool       `json:"active"`
	Upstreams []Upstream `json:"upstreams"`
	Rules     []Rule     `json:"rules"`
	Settings  Settings   `json:"settings"`
}

// Default returns the first-run configuration: public privacy resolvers as a
// working chain until the user adds their NextDNS / Control D profiles.
func Default() *Config {
	return &Config{
		Active: false,
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
			TimeoutMs: 3000, HedgeMs: 1500, FailThreshold: 3, CooldownSec: 15,
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
	if s.HedgeMs <= 0 {
		s.HedgeMs = d.HedgeMs
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
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return Default(), nil
	}
	if err != nil {
		return nil, err
	}
	c := &Config{}
	if err := json.Unmarshal(data, c); err != nil {
		return nil, fmt.Errorf("%s: %w", path, err)
	}
	c.Normalize()
	return c, c.Validate()
}

// Save writes the file atomically.
func (c *Config) Save(path string) error {
	data, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}
