// Package ownports reserves the local UDP ports from which the service sends
// plain DNS itself (for rules that use the original DNS server). The
// interceptor lets these packets through, and the guard does not block them,
// so they can be told apart from every other application's queries.
package ownports

import (
	"errors"
	"math/rand/v2"
	"net"
)

// Low and High bound the reserved range. It lies below the Windows dynamic
// port range (49152 and above), so ephemeral sockets of other programs do not
// normally take these ports.
const (
	Low  = 48600
	High = 48699
)

// Contains reports whether port is one of the reserved ports.
func Contains(port uint16) bool { return port >= Low && port <= High }

// ListenUDP opens a UDP socket on a free port from the reserved range.
func ListenUDP(network string) (*net.UDPConn, error) {
	first := rand.IntN(High - Low + 1)
	for i := 0; i <= High-Low; i++ {
		port := Low + (first+i)%(High-Low+1)
		conn, err := net.ListenUDP(network, &net.UDPAddr{Port: port})
		if err == nil {
			return conn, nil
		}
	}
	return nil, errors.New("no free port in the reserved range for plain DNS")
}
