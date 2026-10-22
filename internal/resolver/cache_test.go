package resolver

import (
	"testing"

	"github.com/miekg/dns"
)

func TestCacheKeySeparatesCheckingDisabled(t *testing.T) {
	a := query()
	b := query()
	b.CheckingDisabled = true
	if cacheKey(a) == cacheKey(b) {
		t.Error("a CD request shares the cache entry of a validating request")
	}
}

func TestNegativeAnswerUsesSOAMinimum(t *testing.T) {
	c := newCache()
	m := new(dns.Msg)
	m.SetRcode(query(), dns.RcodeNameError)
	m.Ns = []dns.RR{&dns.SOA{
		Hdr: dns.RR_Header{Name: "com.", Rrtype: dns.TypeSOA, Class: dns.ClassINET, Ttl: 900},
		Ns:  "a.", Mbox: "b.", Minttl: 60,
	}}
	c.put("k", m, 0, 3600)
	if got := c.entries["k"].ttl.Seconds(); got != 60 {
		t.Errorf("negative TTL %vs, want the SOA minimum of 60s", got)
	}
}
