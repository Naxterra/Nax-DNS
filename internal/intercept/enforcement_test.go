package intercept

import (
	"encoding/binary"
	"fmt"
	"net"
	"net/netip"
	"strings"
	"testing"
	"time"

	"naxdns/internal/config"
	"naxdns/internal/ownports"
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
	if !strings.Contains(f, fmt.Sprintf("udp.SrcPort < %d", ownports.Low)) || !strings.Contains(f, fmt.Sprintf("udp.SrcPort > %d", ownports.High)) {
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
	local, server := netip.MustParseAddr("198.51.100.20"), netip.MustParseAddr("192.0.2.53")
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
	flows.add(src.AddrPort(), src.AddrPort().Addr())
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
	ft.add(k, netip.MustParseAddr("127.0.0.53"))
	if !ft.take(k) || ft.take(k) {
		t.Error("a recorded flow must be accepted exactly once")
	}
}

func TestOnlyTheFirstPacketOfAConnectionIsAccepted(t *testing.T) {
	header := func(flags byte) []byte {
		tcp := make([]byte, 20)
		tcp[13] = flags
		return tcp
	}
	if !isSYN(header(0x02)) {
		t.Error("a SYN does not open a flow")
	}
	for name, flags := range map[string]byte{"SYN-ACK": 0x12, "ACK": 0x10, "data": 0x18, "FIN": 0x11} {
		if isSYN(header(flags)) {
			t.Errorf("a %s packet would renew the flow key", name)
		}
	}
}

func TestAcceptedConnectionRemembersTheDialledServer(t *testing.T) {
	ft := newFlowTable()
	client := netip.MustParseAddrPort("127.0.0.1:50300")
	ft.add(client, netip.MustParseAddr("127.0.0.53"))
	if !ft.take(client) {
		t.Fatal("redirected flow not accepted")
	}
	if dst, ok := ft.destination(client); !ok || dst != netip.MustParseAddr("127.0.0.53") {
		t.Errorf("destination %v, %v; want the dialled 127.0.0.53 rather than the client's own address", dst, ok)
	}
}

// Windows reports a link-local IPv6 peer with its zone index, while the flow
// table is filled from packet headers, which have none.
func TestLinkLocalIPv6ConnectionMatchesItsFlow(t *testing.T) {
	ft := newFlowTable()
	server := netip.MustParseAddr("fe80::1")
	ft.add(netip.AddrPortFrom(server, 50400), server)
	remote, ok := proxyRemote(&net.TCPAddr{IP: net.ParseIP("fe80::1"), Port: 50400, Zone: "12"})
	if !ok || !ft.take(remote) {
		t.Errorf("a redirected connection to a link-local server was refused (%v)", remote)
	}
}

// A flood of connection attempts must not grow the flow table without limit.
func TestFlowTableStaysBounded(t *testing.T) {
	ft := newFlowTable()
	dst := netip.MustParseAddr("192.0.2.53")
	var latest netip.AddrPort
	for i := range maxFlows + 5000 {
		latest = netip.AddrPortFrom(netip.AddrFrom4([4]byte{10, byte(i >> 16), byte(i >> 8), byte(i)}), 5000)
		ft.add(latest, dst)
	}
	if n := len(ft.flows); n > maxFlows {
		t.Errorf("the flow table holds %d pending flows, more than its bound of %d", n, maxFlows)
	}
	if _, ok := ft.flows[latest]; !ok {
		t.Error("the newest connection of the flood was not recorded, so it would be refused")
	}
	now := time.Now()
	for i := range maxFlows {
		ft.accepted[netip.AddrPortFrom(netip.AddrFrom4([4]byte{11, byte(i >> 16), byte(i >> 8), byte(i)}), 5000)] = flowEntry{at: now.Add(time.Duration(i) * time.Microsecond), dst: dst}
	}
	last := maxFlows - 1
	newest := netip.AddrPortFrom(netip.AddrFrom4([4]byte{11, byte(last >> 16), byte(last >> 8), byte(last)}), 5000)
	k := netip.MustParseAddrPort("10.200.0.1:6000")
	ft.flows = map[netip.AddrPort]flowEntry{k: {at: time.Now(), dst: dst}}
	if !ft.take(k) {
		t.Fatal("a fresh flow was refused")
	}
	if n := len(ft.accepted); n > maxFlows {
		t.Errorf("the accepted table holds %d entries, more than its bound of %d", n, maxFlows)
	}
	if _, ok := ft.destination(newest); !ok {
		t.Error("pruning forgot the newest connections instead of the oldest")
	}
}

// Under a flood of connections the expiry sweep runs at most once a second,
// not on every new connection.
func TestFlowTableSweepsAtMostOnceASecond(t *testing.T) {
	ft := newFlowTable()
	dst := netip.MustParseAddr("192.0.2.53")
	old := time.Now().Add(-2 * flowTTL)
	fill := func(base byte) {
		for i := range 5000 {
			ft.flows[netip.AddrPortFrom(netip.AddrFrom4([4]byte{base, 0, byte(i >> 8), byte(i)}), 5000)] = flowEntry{at: old, dst: dst}
		}
	}
	fill(10)
	ft.add(netip.MustParseAddrPort("172.16.0.1:5000"), dst)
	if len(ft.flows) != 1 {
		t.Fatalf("the first sweep left %d entries, want only the new one", len(ft.flows))
	}
	fill(11)
	ft.add(netip.MustParseAddrPort("172.16.0.2:5000"), dst)
	if len(ft.flows) != 5002 {
		t.Errorf("a second sweep ran within the same second (%d entries left)", len(ft.flows))
	}
}
