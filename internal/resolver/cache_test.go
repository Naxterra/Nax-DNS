package resolver

import (
	"strconv"
	"strings"
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
	c.put("k", m, 0, 3600, c.generation())
	if got := c.entries["k"].ttl.Seconds(); got != 60 {
		t.Errorf("negative TTL %vs, want the SOA minimum of 60s", got)
	}
}

// A lookup that started before a reload must not put the previous
// configuration's answer back into the emptied cache.
func TestAnswerFromBeforeAReloadIsNotCached(t *testing.T) {
	c := newCache()
	m := new(dns.Msg)
	m.SetReply(query())
	m.Answer = []dns.RR{&dns.A{Hdr: dns.RR_Header{Name: "example.com.", Rrtype: dns.TypeA, Class: dns.ClassINET, Ttl: 300}}}
	started := c.generation()
	c.flush()
	c.put("k", m, 0, 3600, started)
	if _, _, ok := c.get("k"); ok {
		t.Error("an answer from before the reload was stored after the cache was emptied")
	}
	c.put("k", m, 0, 3600, c.generation())
	if _, _, ok := c.get("k"); !ok {
		t.Error("an answer from the current configuration was not stored")
	}
}

// Answers to requests with EDNS Client Subnet can be scoped to that subnet,
// so they must never be shared with other requests through the cache.
func TestRequestWithClientSubnetBypassesTheCache(t *testing.T) {
	plain := query()
	if !cacheable(plain) {
		t.Fatal("a plain request does not use the cache")
	}
	withECS := query()
	withECS.SetEdns0(1232, false)
	withECS.IsEdns0().Option = append(withECS.IsEdns0().Option, &dns.EDNS0_SUBNET{Code: dns.EDNS0SUBNET, Family: 1, SourceNetmask: 24})
	if cacheable(withECS) {
		t.Error("a request with a client subnet uses the shared cache")
	}
}

// Large answers must not let the cache grow past its memory bound.
func TestCacheStaysWithinItsMemoryBound(t *testing.T) {
	c := newCache()
	big := new(dns.Msg)
	big.SetReply(query())
	for i := range 200 {
		big.Answer = append(big.Answer, &dns.TXT{
			Hdr: dns.RR_Header{Name: "example.com.", Rrtype: dns.TypeTXT, Class: dns.ClassINET, Ttl: 300},
			Txt: []string{strings.Repeat("x", 250), strconv.Itoa(i)},
		})
	}
	size := big.Len()
	for i := range 2 * cacheMaxBytes / size {
		c.put("k"+strconv.Itoa(i), big, 0, 3600, c.generation())
	}
	if c.bytes > cacheMaxBytes {
		t.Errorf("the cache holds %d bytes, more than its bound of %d", c.bytes, cacheMaxBytes)
	}
	total := 0
	for _, e := range c.entries {
		total += e.size
	}
	if total != c.bytes {
		t.Errorf("the cache counts %d bytes but holds %d", c.bytes, total)
	}
}
