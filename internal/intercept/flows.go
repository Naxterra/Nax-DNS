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

// flowTable remembers connections the interceptor redirected to the proxy,
// keyed by the address and port the proxy will see as the remote end.
type flowTable struct {
	mu    sync.Mutex
	flows map[netip.AddrPort]time.Time
}

func newFlowTable() *flowTable {
	return &flowTable{flows: map[netip.AddrPort]time.Time{}}
}

func (t *flowTable) add(remote netip.AddrPort) {
	t.mu.Lock()
	defer t.mu.Unlock()
	now := time.Now()
	t.flows[remote] = now
	if len(t.flows) > 4096 {
		for k, seen := range t.flows {
			if now.Sub(seen) > flowTTL {
				delete(t.flows, k)
			}
		}
	}
}

// take reports whether remote was redirected recently and forgets it.
func (t *flowTable) take(remote netip.AddrPort) bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	seen, ok := t.flows[remote]
	delete(t.flows, remote)
	return ok && time.Since(seen) <= flowTTL
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
