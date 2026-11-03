package resolver

import (
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/miekg/dns"
)

const (
	cacheMaxEntries = 20000
	// cacheMaxBytes bounds the memory of the cached answers, which can be up
	// to 64 KiB each over TCP and the encrypted transports.
	cacheMaxBytes = 32 << 20
	staleWindow   = 6 * time.Hour
)

type cacheEntry struct {
	msg    *dns.Msg
	stored time.Time
	ttl    time.Duration
	size   int
}

type cache struct {
	mu      sync.Mutex
	entries map[string]cacheEntry
	bytes   int
	// gen counts flushes. A lookup passes the generation it started under to
	// put, so an answer that arrives after a flush is not stored.
	gen uint64
}

func newCache() *cache { return &cache{entries: map[string]cacheEntry{}} }

// cacheKey separates requests whose answers may differ: the DO bit asks for
// DNSSEC records, and the CD bit asks the server not to validate, so such an
// answer must never be reused for a request that expects validation.
func cacheKey(req *dns.Msg) string {
	q := req.Question[0]
	do := "0"
	if opt := req.IsEdns0(); opt != nil && opt.Do() {
		do = "1"
	}
	cd := "0"
	if req.CheckingDisabled {
		cd = "1"
	}
	return strings.ToLower(q.Name) + "|" + strconv.Itoa(int(q.Qtype)) + "|" + strconv.Itoa(int(q.Qclass)) + "|" + do + cd
}

// cacheable reports whether the answer to req may be shared through the
// cache. A request with EDNS Client Subnet can get an answer that the server
// scoped to that subnet (RFC 7871), and the key does not separate subnets, so
// such requests bypass the cache in both directions.
func cacheable(req *dns.Msg) bool {
	if opt := req.IsEdns0(); opt != nil {
		for _, o := range opt.Option {
			if o.Option() == dns.EDNS0SUBNET {
				return false
			}
		}
	}
	return true
}

// get returns a copy with TTLs reduced by the entry's age. stale reports an
// expired entry that is still inside the serve-stale window.
func (c *cache) get(key string) (msg *dns.Msg, stale, ok bool) {
	c.mu.Lock()
	e, found := c.entries[key]
	c.mu.Unlock()
	if !found {
		return nil, false, false
	}
	age := time.Since(e.stored)
	if age > e.ttl+staleWindow {
		return nil, false, false
	}
	msg = e.msg.Copy()
	stale = age > e.ttl
	elapsed := uint32(age / time.Second)
	for _, section := range [][]dns.RR{msg.Answer, msg.Ns, msg.Extra} {
		for _, rr := range section {
			h := rr.Header()
			if h.Rrtype == dns.TypeOPT {
				continue
			}
			if stale || h.Ttl <= elapsed {
				h.Ttl = 1
			} else {
				h.Ttl -= elapsed
			}
		}
	}
	return msg, stale, true
}

// put stores msg unless the cache was flushed since generation gen.
func (c *cache) put(key string, msg *dns.Msg, minTTL, maxTTL int, gen uint64) {
	if msg.Truncated || (msg.Rcode != dns.RcodeSuccess && msg.Rcode != dns.RcodeNameError) {
		return
	}
	ttl := uint32(0)
	seen := false
	for _, section := range [][]dns.RR{msg.Answer, msg.Ns} {
		for _, rr := range section {
			if !seen || rr.Header().Ttl < ttl {
				ttl, seen = rr.Header().Ttl, true
			}
		}
	}
	if len(msg.Answer) == 0 {
		// A negative answer lives as long as the smaller of the SOA record's
		// TTL and its MINIMUM field (RFC 2308), and at most five minutes.
		for _, rr := range msg.Ns {
			if soa, ok := rr.(*dns.SOA); ok && soa.Minttl < ttl {
				ttl = soa.Minttl
			}
		}
		if !seen {
			ttl = 30
		}
		ttl = min(ttl, 300)
	} else if !seen {
		ttl = 30
	}
	if int(ttl) < minTTL {
		ttl = uint32(minTTL)
	}
	if int(ttl) > maxTTL {
		ttl = uint32(maxTTL)
	}
	if ttl == 0 {
		return
	}
	size := msg.Len()
	c.mu.Lock()
	defer c.mu.Unlock()
	if gen != c.gen {
		return // the lookup started under a configuration that was replaced
	}
	if old, ok := c.entries[key]; ok {
		c.bytes -= old.size
		delete(c.entries, key)
	}
	if len(c.entries) >= cacheMaxEntries || c.bytes+size > cacheMaxBytes {
		// Drop a tenth of the entries, and more if the new answer still does
		// not fit.
		drop := cacheMaxEntries / 10
		for k, e := range c.entries {
			if drop <= 0 && c.bytes+size <= cacheMaxBytes {
				break
			}
			delete(c.entries, k)
			c.bytes -= e.size
			drop--
		}
	}
	if size > cacheMaxBytes {
		return
	}
	c.entries[key] = cacheEntry{msg: msg.Copy(), stored: time.Now(), ttl: time.Duration(ttl) * time.Second, size: size}
	c.bytes += size
}

func (c *cache) flush() {
	c.mu.Lock()
	c.entries = map[string]cacheEntry{}
	c.bytes = 0
	c.gen++
	c.mu.Unlock()
}

func (c *cache) generation() uint64 {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.gen
}

func (c *cache) size() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return len(c.entries)
}
