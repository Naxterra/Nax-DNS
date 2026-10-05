// Package upstream creates encrypted DNS clients (DoH, DoH3, DoT, DoQ) and
// resolves their host names without ever touching the system resolver.
package upstream

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/netip"
	"net/url"
	"sync"
	"time"

	adg "github.com/AdguardTeam/dnsproxy/upstream"
	"github.com/miekg/dns"

	"naxdns/internal/config"
	"naxdns/internal/ownports"
)

// Server answers DNS queries.
type Server interface {
	Exchange(ctx context.Context, req *dns.Msg) (*dns.Msg, error)
	io.Closer
}

var quiet = slog.New(slog.NewTextHandler(io.Discard, nil))

// Protocol returns the display name for a server URL.
func Protocol(rawURL string) string {
	u, err := url.Parse(rawURL)
	if err != nil {
		return "?"
	}
	switch u.Scheme {
	case "https":
		return "DoH"
	case "h3":
		return "DoH3"
	case "tls":
		return "DoT"
	case "quic":
		return "DoQ"
	case "udp":
		return "Plain"
	}
	return "?"
}

// Bootstrap resolves server host names over DoH endpoints addressed by IP, so
// no plaintext query is needed and the interceptor is never re-entered. The
// last good answer is kept for when the bootstrap endpoints are unreachable.
type Bootstrap struct {
	resolver adg.Resolver
	closers  []io.Closer

	mu       sync.Mutex
	lastGood map[string][]netip.Addr
}

func NewBootstrap(endpoints []string, timeout time.Duration) (*Bootstrap, error) {
	b := &Bootstrap{lastGood: map[string][]netip.Addr{}}
	var parallel adg.ParallelResolver
	for _, e := range endpoints {
		r, err := adg.NewUpstreamResolver(e, &adg.Options{Timeout: timeout, Logger: quiet})
		if err != nil {
			return nil, fmt.Errorf("bootstrap %s: %w", e, err)
		}
		b.closers = append(b.closers, r)
		parallel = append(parallel, adg.NewCachingResolver(r))
	}
	b.resolver = parallel
	return b, nil
}

func (b *Bootstrap) LookupNetIP(ctx context.Context, network, host string) ([]netip.Addr, error) {
	addrs, err := b.resolver.LookupNetIP(ctx, network, host)
	key := network + "|" + host
	b.mu.Lock()
	defer b.mu.Unlock()
	if err == nil && len(addrs) > 0 {
		b.lastGood[key] = addrs
		return addrs, nil
	}
	if old := b.lastGood[key]; len(old) > 0 {
		return old, nil
	}
	if err == nil {
		err = fmt.Errorf("no addresses for %s", host)
	}
	return nil, err
}

func (b *Bootstrap) Close() {
	for _, c := range b.closers {
		c.Close()
	}
}

// New builds the client for one configured server.
func New(u config.Upstream, boot *Bootstrap, timeout time.Duration) (Server, error) {
	parsed, err := url.Parse(u.URL)
	if err != nil {
		return nil, err
	}
	if parsed.Scheme == "udp" {
		port := parsed.Port()
		if port == "" {
			port = "53"
		}
		ap, err := netip.ParseAddrPort(net.JoinHostPort(parsed.Hostname(), port))
		if err != nil {
			return nil, err
		}
		return plain{server: ap, timeout: timeout}, nil
	}
	opts := &adg.Options{Timeout: timeout, Logger: quiet, Bootstrap: boot}
	if len(u.Bootstrap) > 0 {
		var static adg.StaticResolver
		for _, s := range u.Bootstrap {
			a, err := netip.ParseAddr(s)
			if err != nil {
				return nil, err
			}
			static = append(static, a)
		}
		opts.Bootstrap = static
	}
	if parsed.Scheme == "https" {
		opts.HTTPVersions = []adg.HTTPVersion{adg.HTTPVersion2, adg.HTTPVersion11}
	}
	return adg.AddressToUpstream(u.URL, opts)
}

type plain struct {
	server  netip.AddrPort
	timeout time.Duration
}

func (p plain) Exchange(ctx context.Context, req *dns.Msg) (*dns.Msg, error) {
	ctx, cancel := context.WithTimeout(ctx, p.timeout)
	defer cancel()
	return PlainExchange(ctx, p.server, req)
}

func (plain) Close() error { return nil }

// PlainExchange sends req over UDP/53 from a socket the interceptor ignores.
func PlainExchange(ctx context.Context, server netip.AddrPort, req *dns.Msg) (*dns.Msg, error) {
	network := "udp4"
	if server.Addr().Is6() && !server.Addr().Is4In6() {
		network = "udp6"
	}
	conn, err := net.ListenUDP(network, nil)
	if err != nil {
		return nil, err
	}
	defer conn.Close()
	port := uint16(conn.LocalAddr().(*net.UDPAddr).Port)
	ownports.Add(port)
	defer ownports.Remove(port)

	wire, err := req.Pack()
	if err != nil {
		return nil, err
	}
	if deadline, ok := ctx.Deadline(); ok {
		conn.SetDeadline(deadline)
	}
	stop := context.AfterFunc(ctx, func() { conn.SetDeadline(time.Now()) })
	defer stop()
	if _, err := conn.WriteToUDPAddrPort(wire, server); err != nil {
		return nil, err
	}
	buf := make([]byte, 65535)
	for {
		n, from, err := conn.ReadFromUDPAddrPort(buf)
		if err != nil {
			if ctx.Err() != nil {
				return nil, ctx.Err()
			}
			return nil, err
		}
		if from.Addr().Unmap() != server.Addr().Unmap() {
			continue
		}
		resp := new(dns.Msg)
		if err := resp.Unpack(buf[:n]); err != nil || resp.Id != req.Id {
			continue
		}
		return resp, nil
	}
}

// ErrNoServers is returned when no server could be tried at all.
var ErrNoServers = errors.New("no DNS server available")
