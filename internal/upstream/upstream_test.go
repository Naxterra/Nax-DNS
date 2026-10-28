package upstream

import (
	"context"
	"net"
	"net/netip"
	"strconv"
	"testing"
	"time"

	"github.com/miekg/dns"
)

func TestTruncatedPlainAnswerIsFetchedAgainOverTCP(t *testing.T) {
	answer := func(q *dns.Msg, truncated bool) *dns.Msg {
		m := new(dns.Msg)
		m.SetReply(q)
		m.Truncated = truncated
		if !truncated {
			m.Answer = []dns.RR{&dns.TXT{Hdr: dns.RR_Header{Name: q.Question[0].Name, Rrtype: dns.TypeTXT, Class: dns.ClassINET, Ttl: 60}, Txt: []string{"full answer"}}}
		}
		return m
	}
	pc, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer pc.Close()
	port := pc.LocalAddr().(*net.UDPAddr).Port
	ln, err := net.Listen("tcp4", "127.0.0.1:"+strconv.Itoa(port))
	if err != nil {
		t.Skipf("the TCP port next to the UDP test server is taken: %v", err)
	}
	defer ln.Close()
	go func() {
		buf := make([]byte, 512)
		n, from, err := pc.ReadFrom(buf)
		if err != nil {
			return
		}
		q := new(dns.Msg)
		q.Unpack(buf[:n])
		w, _ := answer(q, true).Pack()
		pc.WriteTo(w, from)
	}()
	go func() {
		c, err := ln.Accept()
		if err != nil {
			return
		}
		dc := &dns.Conn{Conn: c}
		defer dc.Close()
		q, err := dc.ReadMsg()
		if err != nil {
			return
		}
		dc.WriteMsg(answer(q, false))
	}()
	req := new(dns.Msg)
	req.SetQuestion("example.com.", dns.TypeTXT)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	resp, err := PlainExchange(ctx, netip.MustParseAddrPort(pc.LocalAddr().String()), req)
	if err != nil {
		t.Fatal(err)
	}
	if resp.Truncated || len(resp.Answer) != 1 {
		t.Errorf("the truncated UDP answer was returned instead of the full TCP answer: %v", resp)
	}
}

func TestPlainExchangeIgnoresRepliesWithTheWrongID(t *testing.T) {
	pc, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer pc.Close()
	go func() {
		buf := make([]byte, 512)
		n, from, err := pc.ReadFrom(buf)
		if err != nil {
			return
		}
		q := new(dns.Msg)
		q.Unpack(buf[:n])
		// A forged answer with another ID arrives first.
		forged := new(dns.Msg)
		forged.SetReply(q)
		forged.Id = q.Id + 1
		forged.Answer = []dns.RR{&dns.A{Hdr: dns.RR_Header{Name: q.Question[0].Name, Rrtype: dns.TypeA, Class: dns.ClassINET, Ttl: 60}, A: net.IPv4(6, 6, 6, 6)}}
		w, _ := forged.Pack()
		pc.WriteTo(w, from)
		real := new(dns.Msg)
		real.SetReply(q)
		real.Answer = []dns.RR{&dns.A{Hdr: dns.RR_Header{Name: q.Question[0].Name, Rrtype: dns.TypeA, Class: dns.ClassINET, Ttl: 60}, A: net.IPv4(192, 0, 2, 1)}}
		w, _ = real.Pack()
		pc.WriteTo(w, from)
	}()
	req := new(dns.Msg)
	req.SetQuestion("example.com.", dns.TypeA)
	req.Id = 4242
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	resp, err := PlainExchange(ctx, netip.MustParseAddrPort(pc.LocalAddr().String()), req)
	if err != nil {
		t.Fatal(err)
	}
	if resp.Id != 4242 {
		t.Errorf("the reply carries ID %d instead of the client's 4242", resp.Id)
	}
	if a, ok := resp.Answer[0].(*dns.A); !ok || !a.A.Equal(net.IPv4(192, 0, 2, 1)) {
		t.Errorf("the forged answer was accepted: %v", resp.Answer)
	}
}
