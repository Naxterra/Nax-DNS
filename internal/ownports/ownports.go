// Package ownports reserves the local UDP ports from which the service sends
// plain DNS itself (for rules that use the original DNS server). The
// interceptor lets these packets through, and the guard does not block them,
// so they can be told apart from every other application's queries.
package ownports

import (
	"errors"
	"math/rand/v2"
	"net"
	"net/netip"
)

// Low and High bound the reserved range. It lies below the Windows dynamic
// port range (49152 and above), so ephemeral sockets of other programs do not
// normally take these ports.
const (
	Low  = 48600
	High = 48999
)

// Contains reports whether port is one of the reserved ports.
func Contains(port uint16) bool { return port >= Low && port <= High }

// DialUDP opens a UDP socket on a free port from the reserved range,
// connected to server, so Windows delivers only datagrams from that server to
// it.
func DialUDP(server netip.AddrPort) (*net.UDPConn, error) {
	network := "udp4"
	if server.Addr().Is6() && !server.Addr().Is4In6() {
		network = "udp6"
	}
	remote := net.UDPAddrFromAddrPort(server)
	first := rand.IntN(High - Low + 1)
	for i := 0; i <= High-Low; i++ {
		port := Low + (first+i)%(High-Low+1)
		conn, err := net.DialUDP(network, &net.UDPAddr{Port: port}, remote)
		if err == nil {
			return conn, nil
		}
	}
	return nil, errors.New("no free port in the reserved range for plain DNS")
}
