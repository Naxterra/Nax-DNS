package intercept

import (
	"encoding/binary"
	"net"
	"net/netip"
	"strings"
	"testing"
	"time"

	"naxdns/internal/config"
)

func TestFiltersCoverLoopbackOnlyWhenAsked(t *testing.T) {
	on := config.Settings{InterceptLoopback: true}
	off := config.Settings{InterceptLoopback: false}
	if strings.Contains(udpFilter(on), "loopback") || strings.Contains(tcpFilter(on, 0), "loopback") || strings.Contains(tcpFilter(on, 5000), "loopback") {
		t.Errorf("loopback excluded although interception of local resolvers is on: %q, %q", udpFilter(on), tcpFilter(on, 5000))
	}
	if !strings.Contains(udpFilter(off), "!loopback") || !strings.Contains(tcpFilter(off, 0), "!loopback") || !strings.Contains(tcpFilter(off, 5000), "!loopback") {
		t.Errorf("loopback not excluded although interception of local resolvers is off")
	}
}

func TestUDPFilterSkipsReservedPorts(t *testing.T) {
	f := udpFilter(config.Settings{})
	if !strings.Contains(f, "udp.SrcPort < 48600") || !strings.Contains(f, "udp.SrcPort > 48699") {
		t.Errorf("own plain-DNS ports are diverted: %q", f)
	}
}

func tcp4Packet(src, dst netip.Addr, sport, dport uint16) ([]byte, ipInfo) {
	pkt := make([]byte, 40)
	pkt[0] = 0x45
	binary.BigEndian.PutUint16(pkt[2:], 40)
	pkt[9] = protoTCP
	copy(pkt[12:], src.AsSlice())
	copy(pkt[16:], dst.AsSlice())
	binary.BigEndian.PutUint16(pkt[20:], sport)
	binary.BigEndian.PutUint16(pkt[22:], dport)
	ip, _ := parseIP(pkt)
	return pkt, ip
}

func TestRedirectReflectsRemoteConnections(t *testing.T) {
	local, server := netip.MustParseAddr("192.168.8.229"), netip.MustParseAddr("10.255.255.1")
	pkt, ip := tcp4Packet(local, server, 50123, 53)
	toProxy, client := redirectTCP(pkt, ip, false, 6000)
	got, _ := parseIP(pkt)
	if !toProxy || got.src != server || got.dst != local || binary.BigEndian.Uint16(pkt[22:]) != 6000 {
		t.Fatalf("client packet not reflected to the proxy: %+v", got)
	}
	if client != netip.AddrPortFrom(server, 50123) {
		t.Errorf("flow key %v, want the server address with the client port", client)
	}

	reply, rip := tcp4Packet(local, server, 6000, 50123)
	if toProxy, _ := redirectTCP(reply, rip, false, 6000); toProxy {
		t.Fatal("proxy reply treated as a client packet")
	}
	got, _ = parseIP(reply)
	if got.src != server || got.dst != local || binary.BigEndian.Uint16(reply[20:]) != 53 {
		t.Errorf("proxy reply does not look like it came from the server's port 53: %+v", got)
	}
}

func TestRedirectKeepsLoopbackAddresses(t *testing.T) {
	lo := netip.MustParseAddr("127.0.0.1")
	pkt, ip := tcp4Packet(lo, lo, 50200, 53)
	toProxy, client := redirectTCP(pkt, ip, true, 6000)
	if !toProxy || binary.BigEndian.Uint16(pkt[22:]) != 6000 || binary.BigEndian.Uint16(pkt[20:]) != 50200 {
		t.Fatal("loopback client packet not sent to the proxy port")
	}
	if client != netip.AddrPortFrom(lo, 50200) {
		t.Errorf("flow key %v, want 127.0.0.1:50200", client)
	}
	reply, rip := tcp4Packet(lo, lo, 6000, 50200)
	redirectTCP(reply, rip, true, 6000)
	if binary.BigEndian.Uint16(reply[20:]) != 53 {
		t.Error("loopback proxy reply not rewritten to port 53")
	}
}

func TestProxyAcceptsOnlyRedirectedConnections(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	flows := newFlowTable()
	guarded := &redirectedOnly{Listener: ln, flows: flows}
	defer guarded.Close()
	accepted := make(chan net.Conn, 2)
	go func() {
		for {
			c, err := guarded.Accept()
			if err != nil {
				return
			}
			accepted <- c
		}
	}()

	// A direct connection, as from another device, must be closed.
	direct, err := net.Dial("tcp", ln.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	direct.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, err := direct.Read(make([]byte, 1)); err == nil {
		t.Error("direct connection was kept open")
	}
	direct.Close()

	// A connection from a source that was recorded as redirected is accepted.
	probe, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	src := probe.Addr().(*net.TCPAddr)
	probe.Close()
	flows.add(src.AddrPort())
	d := net.Dialer{LocalAddr: src}
	conn, err := d.Dial("tcp", ln.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	select {
	case c := <-accepted:
		c.Close()
	case <-time.After(2 * time.Second):
		t.Error("redirected connection was not accepted")
	}
}

func TestFlowTableEntriesAreSingleUse(t *testing.T) {
	ft := newFlowTable()
	k := netip.MustParseAddrPort("10.0.0.1:5000")
	ft.add(k)
	if !ft.take(k) || ft.take(k) {
		t.Error("a recorded flow must be accepted exactly once")
	}
}
