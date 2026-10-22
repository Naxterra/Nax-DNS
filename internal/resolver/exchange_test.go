package resolver

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	"github.com/miekg/dns"

	"naxdns/internal/config"
)

// fakeServer answers with a fixed rcode after a delay, or fails.
type fakeServer struct {
	rcode int
	delay time.Duration
	fail  bool
	calls atomic.Int32
}

func (f *fakeServer) Exchange(ctx context.Context, req *dns.Msg) (*dns.Msg, error) {
	f.calls.Add(1)
	select {
	case <-time.After(f.delay):
	case <-ctx.Done():
		return nil, ctx.Err()
	}
	if f.fail {
		return nil, errors.New("connection refused")
	}
	m := new(dns.Msg)
	m.SetRcode(req, f.rcode)
	return m, nil
}

func (f *fakeServer) Close() error { return nil }

func testState(hedgeMs int, fakes ...*fakeServer) (*state, []*server) {
	st := &state{settings: config.Settings{TimeoutMs: 500, HedgeMs: hedgeMs, FailThreshold: 3, CooldownSec: 15, OnFailure: config.FailServfail}}
	var servers []*server
	for i, f := range fakes {
		servers = append(servers, &server{cfg: config.Upstream{ID: string(rune('a' + i)), Name: string(rune('A' + i))}, client: f})
	}
	st.servers = servers
	return st, servers
}

func query() *dns.Msg {
	m := new(dns.Msg)
	m.SetQuestion("example.com.", dns.TypeA)
	return m
}

func TestServfailIsNotRetriedElsewhere(t *testing.T) {
	primary := &fakeServer{rcode: dns.RcodeServerFailure}
	backup := &fakeServer{rcode: dns.RcodeSuccess}
	st, servers := testState(0, primary, backup)
	resp, srv, _ := (&Resolver{}).exchange(context.Background(), st, servers, query())
	if resp == nil || resp.Rcode != dns.RcodeServerFailure || srv != servers[0] {
		t.Fatalf("want SERVFAIL from the primary, got %v from %v", resp, srv)
	}
	if backup.calls.Load() != 0 {
		t.Fatalf("backup was asked %d times", backup.calls.Load())
	}
}

func TestErrorMovesToBackup(t *testing.T) {
	primary := &fakeServer{fail: true}
	backup := &fakeServer{rcode: dns.RcodeSuccess}
	st, servers := testState(0, primary, backup)
	resp, srv, _ := (&Resolver{}).exchange(context.Background(), st, servers, query())
	if resp == nil || srv != servers[1] {
		t.Fatalf("want the backup's answer, got %v from %v", resp, srv)
	}
}

func TestTimeoutMovesToBackupWithoutHedging(t *testing.T) {
	primary := &fakeServer{rcode: dns.RcodeSuccess, delay: time.Second} // longer than the 500 ms timeout
	backup := &fakeServer{rcode: dns.RcodeSuccess}
	st, servers := testState(0, primary, backup)
	resp, srv, _ := (&Resolver{}).exchange(context.Background(), st, servers, query())
	if resp == nil || srv != servers[1] {
		t.Fatalf("want the backup's answer after the timeout, got %v from %v", resp, srv)
	}
}

func TestSlowPrimaryIsNotRacedWithoutHedging(t *testing.T) {
	primary := &fakeServer{rcode: dns.RcodeSuccess, delay: 200 * time.Millisecond}
	backup := &fakeServer{rcode: dns.RcodeSuccess}
	st, servers := testState(0, primary, backup)
	_, srv, _ := (&Resolver{}).exchange(context.Background(), st, servers, query())
	if srv != servers[0] || backup.calls.Load() != 0 {
		t.Fatalf("want only the primary asked, answer from %v, backup calls %d", srv, backup.calls.Load())
	}
}

func TestHedgingRacesSlowPrimary(t *testing.T) {
	primary := &fakeServer{rcode: dns.RcodeSuccess, delay: 300 * time.Millisecond}
	backup := &fakeServer{rcode: dns.RcodeSuccess}
	st, servers := testState(50, primary, backup)
	_, srv, _ := (&Resolver{}).exchange(context.Background(), st, servers, query())
	if srv != servers[1] {
		t.Fatalf("want the backup to win the race, got %v", srv)
	}
}

func TestHealthyThirdServerIsReachedAfterTwoSilentOnes(t *testing.T) {
	silent1 := &fakeServer{rcode: dns.RcodeSuccess, delay: 5 * time.Second}
	silent2 := &fakeServer{rcode: dns.RcodeSuccess, delay: 5 * time.Second}
	healthy := &fakeServer{rcode: dns.RcodeSuccess}
	st, servers := testState(0, silent1, silent2, healthy)
	resp, srv, detail := (&Resolver{}).exchange(context.Background(), st, servers, query())
	if resp == nil || srv != servers[2] {
		t.Fatalf("want the third server's answer, got %v from %v (%s)", resp, srv, detail)
	}
}

func TestTestRequiresARealAnswer(t *testing.T) {
	ok := new(dns.Msg)
	ok.SetQuestion("example.com.", dns.TypeA)
	ok.Answer = []dns.RR{&dns.A{Hdr: dns.RR_Header{Name: "example.com.", Rrtype: dns.TypeA, Class: dns.ClassINET, Ttl: 60}}}
	if err := checkTestAnswer(ok); err != nil {
		t.Errorf("a real answer failed: %v", err)
	}
	for _, rcode := range []int{dns.RcodeServerFailure, dns.RcodeRefused, dns.RcodeNameError} {
		m := new(dns.Msg)
		m.SetRcode(query(), rcode)
		if checkTestAnswer(m) == nil {
			t.Errorf("%s passed the server test", dns.RcodeToString[rcode])
		}
	}
	empty := new(dns.Msg)
	empty.SetReply(query())
	if checkTestAnswer(empty) == nil {
		t.Error("an empty answer passed the server test")
	}
}

func TestStrictModeNeverReleasesTheOriginalPacket(t *testing.T) {
	for _, onFailure := range []string{config.FailServfail, config.FailPassthrough} {
		r := &Resolver{cache: newCache(), stop: make(chan struct{})}
		st := &state{
			settings: config.Settings{TimeoutMs: 200, OnFailure: onFailure},
			rules:    compileRules([]config.Rule{{Name: "lan", Enabled: true, Domains: []string{"lan"}, Action: config.ActionPassthrough}}),
			byID:     map[string]*server{},
		}
		r.st.Store(st)
		req := new(dns.Msg)
		req.SetQuestion("printer.lan.", dns.TypeA)
		res := r.Resolve(context.Background(), req, Origin{Proto: "udp"})
		strict := onFailure == config.FailServfail
		if res.Passthrough == strict {
			t.Errorf("onFailure=%s: Passthrough=%v", onFailure, res.Passthrough)
		}
	}
}

func TestOverloadedAnswersServfail(t *testing.T) {
	r := &Resolver{cache: newCache()}
	msg := r.Overloaded(query(), Origin{Proto: "udp"}, false)
	if msg.Rcode != dns.RcodeServerFailure {
		t.Errorf("rcode %d", msg.Rcode)
	}
	if e := r.LogSince(0); len(e) != 1 || e[0].Action != "failed" {
		t.Errorf("overloaded query not logged as failed: %+v", e)
	}
}
