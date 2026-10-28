package intercept

import (
	"encoding/binary"
	"net"
	"net/netip"
	"slices"
	"sync"
	"time"
)

// flowTTL is how long a redirected SYN stays valid for the proxy to accept.
const flowTTL = 30 * time.Second

// acceptedTTL is how long the original destination of an accepted connection
// is remembered for its queries.
const acceptedTTL = time.Hour

// maxFlows bounds each map of the flow table, so a flood of connections
// cannot grow it without limit. At the bound the oldest half of the entries
// is forgotten, so new connections are always accepted.
const maxFlows = 16384

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
	pruned   time.Time // the last expiry sweep, so a flood does not sweep on every packet
}

func newFlowTable() *flowTable {
	return &flowTable{flows: map[netip.AddrPort]flowEntry{}, accepted: map[netip.AddrPort]flowEntry{}}
}

func (t *flowTable) add(remote netip.AddrPort, dst netip.Addr) {
	t.mu.Lock()
	defer t.mu.Unlock()
	now := time.Now()
	if (len(t.flows) >= 4096 || len(t.accepted) >= 4096) && now.Sub(t.pruned) > time.Second {
		prune(t.flows, now, flowTTL)
		prune(t.accepted, now, acceptedTTL)
		t.pruned = now
	}
	// At the bound the oldest half goes at once, whenever the last sweep was,
	// so the newest connection is always recorded. That happens at most once
	// per maxFlows/2 new connections.
	if len(t.flows) >= maxFlows {
		prune(t.flows, now, flowTTL)
	}
	t.flows[remote] = flowEntry{at: now, dst: dst}
}

// prune deletes the entries older than ttl and, if the map is still at the
// bound, the oldest half of the rest.
func prune(m map[netip.AddrPort]flowEntry, now time.Time, ttl time.Duration) {
	for k, e := range m {
		if now.Sub(e.at) > ttl {
			delete(m, k)
		}
	}
	if len(m) < maxFlows {
		return
	}
	ages := make([]time.Time, 0, len(m))
	for _, e := range m {
		ages = append(ages, e.at)
	}
	slices.SortFunc(ages, func(a, b time.Time) int { return a.Compare(b) })
	cut := ages[len(ages)/2]
	for k, e := range m {
		if !e.at.After(cut) {
			delete(m, k)
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
	if len(t.accepted) >= maxFlows {
		prune(t.accepted, time.Now(), acceptedTTL)
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
		if remote, ok := proxyRemote(conn.RemoteAddr()); ok && l.flows.take(remote) {
			return conn, nil
		}
		conn.Close()
	}
}

// proxyRemote returns the remote end of a proxy connection in the form the
// flow table uses, which comes from packet headers: no IPv4 mapping and no
// IPv6 zone, which Windows adds for link-local addresses.
func proxyRemote(addr net.Addr) (netip.AddrPort, bool) {
	ra, ok := addr.(*net.TCPAddr)
	if !ok {
		return netip.AddrPort{}, false
	}
	ap := ra.AddrPort()
	return netip.AddrPortFrom(ap.Addr().Unmap().WithZone(""), ap.Port()), true
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
