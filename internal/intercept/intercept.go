// Package intercept captures DNS traffic system-wide with WinDivert. Because
// it works on packets rather than adapter settings, it also sees queries that
// applications send to a VPN's DNS server, before they enter the tunnel.
package intercept

import (
	"context"
	"encoding/binary"
	"fmt"
	"log"
	"net"
	"net/netip"
	"sync"
	"time"

	"github.com/miekg/dns"

	"naxdns/internal/config"
	"naxdns/internal/divert"
	"naxdns/internal/ownports"
	"naxdns/internal/resolver"
)

const (
	maxInFlight  = 512
	queryTimeout = 15 * time.Second
	// Clients that do not announce a UDP size get generous replies; they are
	// delivered locally, so path MTU does not apply.
	defaultUDPSize = 4096
	maxUDPPayload  = 65000
)

// Manager owns the diversion handles and restarts them on settings changes.
type Manager struct {
	res *resolver.Resolver

	mu      sync.Mutex
	running *session
	detail  string
}

func NewManager(res *resolver.Resolver) *Manager {
	return &Manager{res: res, detail: "Off"}
}

// Status reports whether interception is running and a human-readable detail.
func (m *Manager) Status() (bool, string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.running != nil, m.detail
}

// Apply stops any running interception and, if active, starts it again with s.
func (m *Manager) Apply(active bool, s config.Settings) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.running != nil {
		m.running.stop()
		m.running = nil
	}
	m.detail = "Off"
	if !active {
		return nil
	}
	sess, err := start(m.res, s)
	if err != nil {
		m.detail = "Interception failed: " + err.Error()
		return err
	}
	m.running = sess
	m.detail = "Intercepting UDP/53"
	switch s.TCPMode {
	case config.TCPProxy:
		m.detail += " and TCP/53"
	case config.TCPBlock:
		m.detail += ", blocking TCP/53"
	}
	return nil
}

type session struct {
	res    *resolver.Resolver
	ctx    context.Context // cancelled on stop so in-flight queries end promptly
	cancel context.CancelFunc

	udp      *divert.Handle
	udpDone  chan struct{}
	inFlight sync.WaitGroup
	slots    chan struct{}

	tcp       *divert.Handle
	tcpDone   chan struct{}
	tcpServer *dns.Server
	tcpPort   uint16
}

func start(res *resolver.Resolver, s config.Settings) (*session, error) {
	sess := &session{res: res, slots: make(chan struct{}, maxInFlight)}
	sess.ctx, sess.cancel = context.WithCancel(context.Background())

	// QR bit clear: only queries are diverted, never the replies we inject.
	filter := "outbound and udp.DstPort == 53 and udp.PayloadLength >= 12 and udp.Payload[2] < 128"
	if !s.InterceptLoopback {
		filter = "!loopback and " + filter
	}
	var err error
	if sess.udp, err = divert.Open(filter, divert.PriorityHighest, 0); err != nil {
		return nil, err
	}
	sess.udpDone = make(chan struct{})
	go sess.udpLoop()

	switch s.TCPMode {
	case config.TCPBlock:
		sess.tcp, err = divert.Open("outbound and !loopback and tcp.DstPort == 53", divert.PriorityHighest, divert.FlagDrop)
	case config.TCPProxy:
		err = sess.startTCPProxy()
	}
	if err != nil {
		sess.stop()
		return nil, err
	}
	return sess, nil
}

func (s *session) stop() {
	s.udp.Shutdown()
	<-s.udpDone
	s.cancel()
	if s.tcp != nil {
		s.tcp.Shutdown()
		if s.tcpDone != nil {
			<-s.tcpDone
		}
		s.tcp.Close()
	}
	if s.tcpServer != nil {
		s.tcpServer.Shutdown()
	}
	s.inFlight.Wait()
	s.udp.Close()
}

func (s *session) udpLoop() {
	defer close(s.udpDone)
	buf := make([]byte, 65535)
	for {
		var addr divert.Address
		n, err := s.udp.Recv(buf, &addr)
		if err != nil {
			return
		}
		pkt := append([]byte(nil), buf[:n]...)
		ip, ok := parseIP(pkt)
		if !ok || ip.proto != protoUDP || len(pkt) < ip.hdrLen+8+12 ||
			ownports.Contains(binary.BigEndian.Uint16(pkt[ip.hdrLen:])) {
			s.udp.Send(pkt, &addr)
			continue
		}
		select {
		case s.slots <- struct{}{}:
		default:
			s.udp.Send(pkt, &addr) // overloaded: do not hold DNS hostage
			continue
		}
		s.inFlight.Add(1)
		go func() {
			defer func() { <-s.slots; s.inFlight.Done() }()
			s.handleUDP(pkt, ip, addr)
		}()
	}
}

func (s *session) handleUDP(pkt []byte, ip ipInfo, addr divert.Address) {
	req := new(dns.Msg)
	if err := req.Unpack(pkt[ip.hdrLen+8:]); err != nil {
		s.udp.Send(pkt, &addr)
		return
	}
	ctx, cancel := context.WithTimeout(s.ctx, queryTimeout)
	defer cancel()
	res := s.res.Resolve(ctx, req, resolver.Origin{Proto: "udp", Server: netip.AddrPortFrom(ip.dst, 53)})
	if res.Passthrough {
		s.udp.Send(pkt, &addr)
		return
	}
	limit := defaultUDPSize
	if opt := req.IsEdns0(); opt != nil {
		limit = max(int(opt.UDPSize()), dns.MinMsgSize)
	}
	res.Msg.Truncate(min(limit, maxUDPPayload))
	wire, err := res.Msg.Pack()
	if err != nil {
		log.Printf("intercept: pack reply for %s: %v", req.Question[0].Name, err)
		return
	}
	// Loopback traffic is "outbound" in both directions on Windows; everything
	// else is delivered as an inbound packet from the addressed server.
	addr.SetOutbound(addr.Loopback())
	if err := s.udp.Send(udpReply(pkt, ip, wire), &addr); err != nil {
		log.Printf("intercept: inject reply: %v", err)
	}
}

// startTCPProxy reflects outbound TCP/53 connections into a local DNS-over-TCP
// server: the SYN's addresses are swapped and it is delivered inbound to our
// listener, whose replies are rewritten back to look like the remote server.
func (s *session) startTCPProxy() error {
	ln, err := net.Listen("tcp", ":0")
	if err != nil {
		return err
	}
	s.tcpPort = uint16(ln.Addr().(*net.TCPAddr).Port)
	s.tcpServer = &dns.Server{Listener: ln, Handler: dns.HandlerFunc(s.serveTCP), ReadTimeout: 10 * time.Second}
	go s.tcpServer.ActivateAndServe()

	filter := fmt.Sprintf("outbound and !loopback and tcp and (tcp.DstPort == 53 or tcp.SrcPort == %d)", s.tcpPort)
	if s.tcp, err = divert.Open(filter, divert.PriorityHighest, 0); err != nil {
		return err
	}
	s.tcpDone = make(chan struct{})
	go s.tcpLoop()
	return nil
}

func (s *session) tcpLoop() {
	defer close(s.tcpDone)
	buf := make([]byte, 65535)
	for {
		var addr divert.Address
		n, err := s.tcp.Recv(buf, &addr)
		if err != nil {
			return
		}
		pkt := buf[:n]
		ip, ok := parseIP(pkt)
		if !ok || ip.proto != protoTCP || len(pkt) < ip.hdrLen+20 {
			s.tcp.Send(pkt, &addr)
			continue
		}
		tcp := pkt[ip.hdrLen:]
		if binary.BigEndian.Uint16(tcp[2:]) == 53 {
			binary.BigEndian.PutUint16(tcp[2:], s.tcpPort) // client → proxy
		} else {
			binary.BigEndian.PutUint16(tcp[0:], 53) // proxy → client
		}
		swapAddrs(pkt, ip)
		addr.SetOutbound(false)
		s.tcp.Send(pkt, &addr)
	}
}

func (s *session) serveTCP(w dns.ResponseWriter, req *dns.Msg) {
	// After reflection the connection's remote address is the DNS server the
	// application dialled.
	origin := resolver.Origin{Proto: "tcp"}
	if ra, ok := w.RemoteAddr().(*net.TCPAddr); ok {
		if a, ok := netip.AddrFromSlice(ra.IP); ok {
			origin.Server = netip.AddrPortFrom(a.Unmap(), 53)
		}
	}
	ctx, cancel := context.WithTimeout(s.ctx, queryTimeout)
	defer cancel()
	res := s.res.Resolve(ctx, req, origin)
	if res.Msg != nil {
		w.WriteMsg(res.Msg)
	}
}
