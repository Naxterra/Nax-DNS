// Package intercept captures DNS traffic system-wide with WinDivert. Because
// it works on packets rather than adapter settings, it also sees queries that
// applications send to a VPN's DNS server, before they enter the tunnel.
package intercept

import (
	"context"
	"errors"
	"fmt"
	"log"
	"net"
	"net/netip"
	"sync"
	"sync/atomic"
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
	restartDelay   = 2 * time.Second
)

// Manager owns the diversion handles. It restarts them on settings changes
// and when a capture loop ends unexpectedly.
type Manager struct {
	res *resolver.Resolver

	mu       sync.Mutex
	running  *session
	detail   string
	want     bool
	settings config.Settings
	retrying bool
}

func NewManager(res *resolver.Resolver) *Manager {
	return &Manager{res: res, detail: "Off"}
}

// Status reports whether interception is running and a human-readable detail.
func (m *Manager) Status() (bool, string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	detail := m.detail
	if m.running != nil {
		if p, _ := m.running.tcpProblem.Load().(string); p != "" {
			detail += ", but " + p
		}
	}
	return m.running != nil, detail
}

// Apply stops any running interception and, if active, starts it again with s.
func (m *Manager) Apply(active bool, s config.Settings) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.want, m.settings = active, s
	return m.restartLocked()
}

func (m *Manager) restartLocked() error {
	if m.running != nil {
		m.running.stop()
		m.running = nil
	}
	m.detail = "Off"
	if !m.want {
		return nil
	}
	sess, err := start(m.res, m.settings, m.failed)
	if err != nil {
		m.detail = "Interception failed: " + err.Error()
		// The driver may become available later, for example once the base
		// filtering engine has started at boot.
		if !m.retrying {
			m.retrying = true
			go m.retry(err)
		}
		return err
	}
	m.running = sess
	m.detail = "Intercepting UDP/53"
	switch m.settings.TCPMode {
	case config.TCPProxy:
		m.detail += " and TCP/53"
	case config.TCPBlock:
		m.detail += ", blocking TCP/53"
	}
	return nil
}

// failed is called when a capture loop of sess stopped on its own. The
// session is torn down, reported, and started again after a short delay.
func (m *Manager) failed(sess *session, err error) {
	m.mu.Lock()
	if m.running != sess {
		m.mu.Unlock()
		return
	}
	log.Printf("intercept: capture stopped unexpectedly: %v", err)
	sess.stop()
	m.running = nil
	m.detail = "Interception failed: " + err.Error()
	retrying := m.retrying
	m.retrying = true
	m.mu.Unlock()
	if !retrying {
		m.retry(err)
	}
}

// retry starts interception again every few seconds until it runs or is no
// longer wanted. Each distinct error is logged once.
func (m *Manager) retry(last error) {
	for {
		time.Sleep(restartDelay)
		m.mu.Lock()
		if !m.want || m.running != nil {
			m.retrying = false
			m.mu.Unlock()
			return
		}
		// retrying stays true here, so a failing restartLocked does not start
		// a second retry loop.
		err := m.restartLocked()
		if err == nil {
			m.retrying = false
			m.mu.Unlock()
			log.Printf("intercept: capture restarted")
			return
		}
		m.mu.Unlock()
		if last == nil || err.Error() != last.Error() {
			log.Printf("intercept: restart failed: %v", err)
		}
		last = err
	}
}

type session struct {
	res      *resolver.Resolver
	settings config.Settings
	strict   bool
	onFail   func(*session, error)
	stopping atomic.Bool
	ctx      context.Context // cancelled on stop so in-flight queries end promptly
	cancel   context.CancelFunc

	udp      *divert.Handle
	udpDone  chan struct{}
	inFlight sync.WaitGroup
	slots    chan struct{}

	tcp         *divert.Handle
	tcpDone     chan struct{}
	tcpServer   *dns.Server
	tcpProblem  atomic.Value // string: why TCP interception does not fully work
	tcpListener net.Listener
	tcpPort     uint16
	flows       *flowTable
}

// ProxyPort is the TCP port of the built-in DNS-over-TCP proxy. The installer
// opens Windows Firewall for the service on this port only.
const ProxyPort = 48653

// udpFilter selects DNS queries (QR bit clear), never the replies we inject
// and never the service's own plain-DNS sockets.
func udpFilter(s config.Settings) string {
	filter := fmt.Sprintf("outbound and udp.DstPort == 53 and udp.PayloadLength >= 12 and udp.Payload[2] < 128 and (udp.SrcPort < %d or udp.SrcPort > %d)",
		ownports.Low, ownports.High)
	if !s.InterceptLoopback {
		filter = "!loopback and " + filter
	}
	return filter
}

// tcpFilter selects TCP/53 connections and, in proxy mode, the proxy's
// replies on port proxyPort.
func tcpFilter(s config.Settings, proxyPort uint16) string {
	// The service's own TCP queries come from the reserved ports.
	query := fmt.Sprintf("(tcp.DstPort == 53 and (tcp.SrcPort < %d or tcp.SrcPort > %d))", ownports.Low, ownports.High)
	match := "tcp and " + query
	if proxyPort != 0 {
		match = fmt.Sprintf("tcp and (%s or tcp.SrcPort == %d)", query, proxyPort)
	}
	if !s.InterceptLoopback {
		return "outbound and !loopback and " + match
	}
	return "outbound and " + match
}

func start(res *resolver.Resolver, s config.Settings, onFail func(*session, error)) (*session, error) {
	sess := &session{
		res: res, settings: s, strict: s.OnFailure == config.FailServfail, onFail: onFail,
		slots: make(chan struct{}, maxInFlight),
	}
	sess.ctx, sess.cancel = context.WithCancel(context.Background())

	var err error
	if sess.udp, err = divert.Open(udpFilter(s), divert.PriorityHighest, 0); err != nil {
		return nil, err
	}
	sess.udpDone = make(chan struct{})
	go sess.udpLoop()

	switch s.TCPMode {
	case config.TCPBlock:
		sess.tcp, err = divert.Open(tcpFilter(s, 0), divert.PriorityHighest, divert.FlagDrop)
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
	s.stopping.Store(true)
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
	// Shutdown does nothing if the server had not started serving yet, so
	// the listener is closed explicitly as well.
	if s.tcpListener != nil {
		s.tcpListener.Close()
	}
	s.inFlight.Wait()
	s.udp.Close()
}

// loopEnded reports a capture loop that returned while the session was not
// being stopped.
func (s *session) loopEnded(err error) {
	if s.stopping.Load() {
		return
	}
	if err == nil {
		err = errors.New("capture loop ended")
	}
	go s.onFail(s, err)
}

// release decides what happens to a diverted packet that is not answered.
// Outside strict mode it continues to the server it was sent to. In strict
// mode it is dropped, so nothing reaches another DNS server.
func (s *session) release(h *divert.Handle, pkt []byte, addr *divert.Address) {
	if !s.strict {
		h.Send(pkt, addr)
	}
}

func (s *session) udpLoop() {
	defer close(s.udpDone)
	buf := make([]byte, 65535)
	for {
		var addr divert.Address
		n, err := s.udp.Recv(buf, &addr)
		if err != nil {
			s.loopEnded(err)
			return
		}
		pkt := append([]byte(nil), buf[:n]...)
		ip, ok := parseIP(pkt)
		if !ok || ip.proto != protoUDP || len(pkt) < ip.hdrLen+8+12 {
			s.release(s.udp, pkt, &addr)
			continue
		}
		select {
		case s.slots <- struct{}{}:
		default:
			s.overloaded(pkt, ip, addr)
			continue
		}
		s.inFlight.Add(1)
		go func() {
			defer func() { <-s.slots; s.inFlight.Done() }()
			s.handleUDP(pkt, ip, addr)
		}()
	}
}

// overloaded handles a query that arrives while every slot is busy. Outside
// strict mode it goes to the original server; in strict mode it is answered
// with SERVFAIL at once, so the application retries instead of the query
// leaving unencrypted.
func (s *session) overloaded(pkt []byte, ip ipInfo, addr divert.Address) {
	req := new(dns.Msg)
	if err := req.Unpack(pkt[ip.hdrLen+8:]); err != nil || len(req.Question) != 1 {
		s.release(s.udp, pkt, &addr)
		return
	}
	origin := resolver.Origin{Proto: "udp", Server: netip.AddrPortFrom(ip.dst, 53)}
	if !s.strict {
		s.res.Overloaded(req, origin, true)
		s.udp.Send(pkt, &addr)
		return
	}
	s.reply(pkt, ip, addr, req, s.res.Overloaded(req, origin, false))
}

func (s *session) handleUDP(pkt []byte, ip ipInfo, addr divert.Address) {
	req := new(dns.Msg)
	if err := req.Unpack(pkt[ip.hdrLen+8:]); err != nil {
		s.release(s.udp, pkt, &addr)
		return
	}
	ctx, cancel := context.WithTimeout(s.ctx, queryTimeout)
	defer cancel()
	res := s.res.Resolve(ctx, req, resolver.Origin{Proto: "udp", Server: netip.AddrPortFrom(ip.dst, 53)})
	if res.Passthrough {
		// The resolver only asks for this outside strict mode, but this
		// session may be a strict one that is about to be replaced.
		s.release(s.udp, pkt, &addr)
		return
	}
	s.reply(pkt, ip, addr, req, res.Msg)
}

func (s *session) reply(pkt []byte, ip ipInfo, addr divert.Address, req, msg *dns.Msg) {
	limit := defaultUDPSize
	if opt := req.IsEdns0(); opt != nil {
		limit = max(int(opt.UDPSize()), dns.MinMsgSize)
	}
	msg.Truncate(min(limit, maxUDPPayload))
	wire, err := msg.Pack()
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

// startTCPProxy redirects TCP/53 connections into a local DNS-over-TCP
// server. A connection to a remote server is reflected: the SYN's addresses
// are swapped and it is delivered inbound to the listener, whose replies are
// rewritten to look like the remote server. A loopback connection only has its
// port changed. The listener accepts nothing but these redirected flows.
func (s *session) startTCPProxy() error {
	// The installer's firewall rule admits redirected connections only on
	// ProxyPort. Another port works only if Windows Firewall allows it.
	ln, err := net.Listen("tcp", fmt.Sprintf(":%d", ProxyPort))
	if err != nil {
		log.Printf("intercept: TCP proxy port %d is busy (%v); using a random port", ProxyPort, err)
		if ln, err = net.Listen("tcp", ":0"); err != nil {
			return err
		}
		s.tcpProblem.Store(fmt.Sprintf("the TCP/53 proxy runs on port %d, which the firewall rule does not cover, so TCP DNS may fail", ln.Addr().(*net.TCPAddr).Port))
	}
	s.tcpListener = ln
	s.tcpPort = uint16(ln.Addr().(*net.TCPAddr).Port)
	s.flows = newFlowTable()
	s.tcpServer = &dns.Server{
		Listener: &redirectedOnly{Listener: ln, flows: s.flows},
		Handler:  dns.HandlerFunc(s.serveTCP), ReadTimeout: 10 * time.Second,
	}
	go func() {
		if err := s.tcpServer.ActivateAndServe(); err != nil && !s.stopping.Load() {
			log.Printf("intercept: TCP proxy stopped: %v", err)
			s.tcpProblem.Store("the TCP/53 proxy stopped (" + err.Error() + "), so TCP DNS fails")
		}
	}()

	if s.tcp, err = divert.Open(tcpFilter(s.settings, s.tcpPort), divert.PriorityHighest, 0); err != nil {
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
			s.loopEnded(err)
			return
		}
		pkt := buf[:n]
		ip, ok := parseIP(pkt)
		if !ok || ip.proto != protoTCP || len(pkt) < ip.hdrLen+20 {
			s.release(s.tcp, pkt, &addr)
			continue
		}
		toProxy, client := redirectTCP(pkt, ip, addr.Loopback(), s.tcpPort)
		// Only a connection's first packet makes it acceptable, so later
		// packets of the same flow cannot renew the key after the accept.
		if toProxy && isSYN(pkt[ip.hdrLen:]) {
			s.flows.add(client, ip.dst.Unmap())
		}
		addr.SetOutbound(addr.Loopback())
		s.tcp.Send(pkt, &addr)
	}
}

func (s *session) serveTCP(w dns.ResponseWriter, req *dns.Msg) {
	// The flow table knows which server the application dialled. On loopback
	// the connection's remote address is only the client's own address, and
	// a local resolver may listen on another loopback address such as
	// 127.0.0.53.
	origin := resolver.Origin{Proto: "tcp"}
	if remote, ok := proxyRemote(w.RemoteAddr()); ok {
		dst, ok := s.flows.destination(remote)
		if !ok {
			dst = remote.Addr()
		}
		origin.Server = netip.AddrPortFrom(dst, 53)
	}
	ctx, cancel := context.WithTimeout(s.ctx, queryTimeout)
	defer cancel()
	res := s.res.Resolve(ctx, req, origin)
	if res.Msg != nil {
		w.WriteMsg(res.Msg)
	}
}
