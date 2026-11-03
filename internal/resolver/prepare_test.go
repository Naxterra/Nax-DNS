package resolver

import (
	"context"
	"testing"

	"github.com/miekg/dns"

	"naxdns/internal/config"
)

// A configuration that is prepared while its save is still pending must not
// answer any query until it is published.
func TestPreparedConfigurationIsUnusedUntilPublished(t *testing.T) {
	r := &Resolver{cache: newCache(), stop: make(chan struct{})}
	r.st.Store(&state{settings: config.Settings{TimeoutMs: 200, OnFailure: config.FailServfail}, rules: compileRules(nil), byID: map[string]*server{}})
	cfg := config.Default()
	cfg.Upstreams = nil
	cfg.Rules = []config.Rule{{ID: "t", Name: "test", Enabled: true, Domains: []string{"blocked.example"}, Action: config.ActionBlock}}
	p, err := r.Prepare(cfg)
	if err != nil {
		t.Fatal(err)
	}
	ask := func() int {
		q := new(dns.Msg)
		q.SetQuestion("a.blocked.example.", dns.TypeA)
		return r.Resolve(context.Background(), q, Origin{Proto: "test"}).Msg.Rcode
	}
	if got := ask(); got == dns.RcodeNameError {
		t.Fatal("the prepared rule answered a query before it was published")
	}
	r.Publish(p)
	if got := ask(); got != dns.RcodeNameError {
		t.Errorf("after publishing, the rule's domain got rcode %s, want NXDOMAIN", dns.RcodeToString[got])
	}
	closeState(r.st.Load())
}
