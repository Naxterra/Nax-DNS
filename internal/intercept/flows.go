package intercept

import (
	"encoding/binary"
	"net"
	"net/netip"
	"sync"
	"time"
)

// flowTTL is how long a redirected SYN stays valid for the proxy to accept.
const flowTTL = 30 * time.Second

// acceptedTTL is how long the original destination of an accepted connection
// is remembered for its queries.
const acceptedTTL = time.Hour

type flowEntry struct {
	at  time.Time
	dst netip.Addr // the DNS server the application dialled
}

// flowTable remembers connections the interceptor redirected to the proxy,
// keyed by the address and port the proxy will see as the remote end, and
// the server each accepted connection was originally meant for.
type flowTable struct {
	mu       sync.Mutex
	flows    map[netip.AddrPort]flowEntry
	accepted map[netip.AddrPort]flowEntry
}

func newFlowTable() *flowTable {
	return &flowTable{flows: map[netip.AddrPort]flowEntry{}, accepted: map[netip.AddrPort]flowEntry{}}
}

func (t *flowTable) add(remote netip.AddrPort, dst netip.Addr) {
	t.mu.Lock()
	defer t.mu.Unlock()
	now := time.Now()
	t.flows[remote] = flowEntry{at: now, dst: dst}
	if len(t.flows) > 4096 {
		for k, e := range t.flows {
			if now.Sub(e.at) > flowTTL {
				delete(t.flows, k)
			}
		}
	}
	if len(t.accepted) > 4096 {
		for k, e := range t.accepted {
			if now.Sub(e.at) > acceptedTTL {
				delete(t.accepted, k)
			}
		}
	}
}

// take reports whether remote was redirected recently, forgets the pending
// redirect and remembers the connection's original destination.
func (t *flowTable) take(remote netip.AddrPort) bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	e, ok := t.flows[remote]
	delete(t.flows, remote)
	if !ok || time.Since(e.at) > flowTTL {
		return false
	}
	t.accepted[remote] = flowEntry{at: time.Now(), dst: e.dst}
	return true
}

// destination returns the server an accepted connection was dialled to.
func (t *flowTable) destination(remote netip.AddrPort) (netip.Addr, bool) {
	t.mu.Lock()
	defer t.mu.Unlock()
	e, ok := t.accepted[remote]
	return e.dst, ok
}

// redirectedOnly wraps the proxy's listener and closes every connection the
// interceptor did not redirect, such as one opened directly from another
// device on the network.
type redirectedOnly struct {
	net.Listener
	flows *flowTable
}

func (l *redirectedOnly) Accept() (net.Conn, error) {
	for {
		conn, err := l.Listener.Accept()
		if err != nil {
			return nil, err
		}
		if ra, ok := conn.RemoteAddr().(*net.TCPAddr); ok {
			if ap := ra.AddrPort(); l.flows.take(netip.AddrPortFrom(ap.Addr().Unmap(), ap.Port())) {
				return conn, nil
			}
		}
		conn.Close()
	}
}

// isSYN reports whether a TCP header opens a connection (SYN set, ACK clear).
func isSYN(tcp []byte) bool {
	const syn, ack = 0x02, 0x10
	return len(tcp) > 13 && tcp[13]&syn != 0 && tcp[13]&ack == 0
}

// redirectTCP rewrites one packet of a TCP/53 connection in place. A packet
// to port 53 goes to the proxy; a packet from the proxy port goes back to the
// client as if it came from port 53. Connections to a remote server are
// reflected by swapping the addresses; loopback connections keep them.
// toProxy is true for client packets, and client is the remote address the
// proxy will see for the connection.
func redirectTCP(pkt []byte, ip ipInfo, loopback bool, proxyPort uint16) (toProxy bool, client netip.AddrPort) {
	tcp := pkt[ip.hdrLen:]
	if binary.BigEndian.Uint16(tcp[2:]) == 53 {
		toProxy = true
		binary.BigEndian.PutUint16(tcp[2:], proxyPort)
		srcPort := binary.BigEndian.Uint16(tcp[0:])
		// After a reflection the proxy sees the original server as the remote
		// end; on loopback it sees the client's own address.
		remote := ip.dst
		if loopback {
			remote = ip.src
		}
		client = netip.AddrPortFrom(remote.Unmap(), srcPort)
	} else {
		binary.BigEndian.PutUint16(tcp[0:], 53)
	}
	if !loopback {
		swapAddrs(pkt, ip)
	}
	return toProxy, client
}
