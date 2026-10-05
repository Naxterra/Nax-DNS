package intercept

import (
	"encoding/binary"
	"net/netip"
)

const (
	protoTCP = 6
	protoUDP = 17
)

// ipInfo locates the transport header of an unfragmented IPv4/IPv6 packet.
type ipInfo struct {
	v6       bool
	hdrLen   int
	proto    byte
	src, dst netip.Addr
}

func parseIP(pkt []byte) (ipInfo, bool) {
	if len(pkt) < 20 {
		return ipInfo{}, false
	}
	switch pkt[0] >> 4 {
	case 4:
		ihl := int(pkt[0]&0x0f) * 4
		if ihl < 20 || len(pkt) < ihl || binary.BigEndian.Uint16(pkt[6:])&0x3fff != 0 {
			return ipInfo{}, false // malformed or fragmented
		}
		src, _ := netip.AddrFromSlice(pkt[12:16])
		dst, _ := netip.AddrFromSlice(pkt[16:20])
		return ipInfo{hdrLen: ihl, proto: pkt[9], src: src, dst: dst}, true
	case 6:
		if len(pkt) < 40 {
			return ipInfo{}, false
		}
		src, _ := netip.AddrFromSlice(pkt[8:24])
		dst, _ := netip.AddrFromSlice(pkt[24:40])
		// Extension headers are not followed; such packets are passed on untouched.
		return ipInfo{v6: true, hdrLen: 40, proto: pkt[6], src: src, dst: dst}, true
	}
	return ipInfo{}, false
}

// swapAddrs exchanges source and destination address in place.
func swapAddrs(pkt []byte, ip ipInfo) {
	if ip.v6 {
		var tmp [16]byte
		copy(tmp[:], pkt[8:24])
		copy(pkt[8:24], pkt[24:40])
		copy(pkt[24:40], tmp[:])
		return
	}
	var tmp [4]byte
	copy(tmp[:], pkt[12:16])
	copy(pkt[12:16], pkt[16:20])
	copy(pkt[16:20], tmp[:])
}

// udpReply builds the answer to a diverted UDP query: addresses and ports
// reversed, so it looks like it came from the server the client asked.
// Checksums are left zero for WinDivert to fill in.
func udpReply(query []byte, ip ipInfo, payload []byte) []byte {
	sport := query[ip.hdrLen : ip.hdrLen+2]
	dport := query[ip.hdrLen+2 : ip.hdrLen+4]
	udpLen := 8 + len(payload)
	var out []byte
	var udp []byte
	if ip.v6 {
		out = make([]byte, 40+udpLen)
		copy(out, query[:40])
		binary.BigEndian.PutUint16(out[4:], uint16(udpLen))
		out[7] = 64
		swapAddrs(out, ip)
		udp = out[40:]
	} else {
		out = make([]byte, 20+udpLen)
		out[0] = 0x45
		binary.BigEndian.PutUint16(out[2:], uint16(20+udpLen))
		out[8] = 64
		out[9] = protoUDP
		copy(out[12:16], query[16:20])
		copy(out[16:20], query[12:16])
		udp = out[20:]
	}
	copy(udp[0:2], dport)
	copy(udp[2:4], sport)
	binary.BigEndian.PutUint16(udp[4:], uint16(udpLen))
	copy(udp[8:], payload)
	return out
}
