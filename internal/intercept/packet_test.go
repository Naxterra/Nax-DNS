package intercept

import (
	"bytes"
	"encoding/binary"
	"net/netip"
	"testing"
)

func udp4Query(payload []byte) []byte {
	pkt := make([]byte, 28+len(payload))
	pkt[0] = 0x45
	binary.BigEndian.PutUint16(pkt[2:], uint16(len(pkt)))
	pkt[8], pkt[9] = 128, protoUDP
	copy(pkt[12:], []byte{10, 0, 0, 2})
	copy(pkt[16:], []byte{10, 255, 255, 1})
	binary.BigEndian.PutUint16(pkt[20:], 50000)
	binary.BigEndian.PutUint16(pkt[22:], 53)
	binary.BigEndian.PutUint16(pkt[24:], uint16(8+len(payload)))
	copy(pkt[28:], payload)
	return pkt
}

func TestUDPReplyIPv4ReversesEndpoints(t *testing.T) {
	query := udp4Query([]byte("question-bytes"))
	ip, ok := parseIP(query)
	if !ok || ip.proto != protoUDP || ip.dst != netip.MustParseAddr("10.255.255.1") {
		t.Fatalf("parseIP = %+v, %v", ip, ok)
	}
	answer := []byte("a-longer-answer-payload")
	reply := udpReply(query, ip, answer)
	rip, ok := parseIP(reply)
	if !ok || rip.src != ip.dst || rip.dst != ip.src {
		t.Fatalf("reply addresses not reversed: %+v", rip)
	}
	if got := binary.BigEndian.Uint16(reply[2:]); int(got) != len(reply) {
		t.Errorf("total length = %d, want %d", got, len(reply))
	}
	udp := reply[rip.hdrLen:]
	if binary.BigEndian.Uint16(udp[0:]) != 53 || binary.BigEndian.Uint16(udp[2:]) != 50000 {
		t.Errorf("ports not reversed: % x", udp[:4])
	}
	if int(binary.BigEndian.Uint16(udp[4:])) != 8+len(answer) || !bytes.Equal(udp[8:], answer) {
		t.Errorf("bad UDP length or payload")
	}
}

func TestUDPReplyIPv6(t *testing.T) {
	payload := []byte("question-bytes")
	query := make([]byte, 48+len(payload))
	query[0] = 0x60
	binary.BigEndian.PutUint16(query[4:], uint16(8+len(payload)))
	query[6], query[7] = protoUDP, 128
	src, dst := netip.MustParseAddr("fd00::2"), netip.MustParseAddr("fd00::53")
	copy(query[8:], src.AsSlice())
	copy(query[24:], dst.AsSlice())
	binary.BigEndian.PutUint16(query[40:], 50001)
	binary.BigEndian.PutUint16(query[42:], 53)
	copy(query[48:], payload)

	ip, ok := parseIP(query)
	if !ok || !ip.v6 || ip.dst != dst {
		t.Fatalf("parseIP = %+v, %v", ip, ok)
	}
	reply := udpReply(query, ip, []byte("answer"))
	rip, _ := parseIP(reply)
	if rip.src != dst || rip.dst != src {
		t.Fatalf("reply addresses not reversed: %+v", rip)
	}
	if binary.BigEndian.Uint16(reply[4:]) != 8+6 || binary.BigEndian.Uint16(reply[40:]) != 53 || binary.BigEndian.Uint16(reply[42:]) != 50001 {
		t.Errorf("bad IPv6 reply header")
	}
}

func TestParseIPRejectsFragments(t *testing.T) {
	query := udp4Query([]byte("0123456789ab"))
	binary.BigEndian.PutUint16(query[6:], 0x2000) // more fragments
	if _, ok := parseIP(query); ok {
		t.Error("fragment accepted")
	}
}
