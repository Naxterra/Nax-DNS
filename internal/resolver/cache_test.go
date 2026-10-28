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
	c.put("k", m, 0, 3600)
	if got := c.entries["k"].ttl.Seconds(); got != 60 {
		t.Errorf("negative TTL %vs, want the SOA minimum of 60s", got)
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
		c.put("k"+strconv.Itoa(i), big, 0, 3600)
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
