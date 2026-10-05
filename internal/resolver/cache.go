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
	staleWindow     = 6 * time.Hour
)

type cacheEntry struct {
	msg    *dns.Msg
	stored time.Time
	ttl    time.Duration
}

type cache struct {
	mu      sync.Mutex
	entries map[string]cacheEntry
}

func newCache() *cache { return &cache{entries: map[string]cacheEntry{}} }

func cacheKey(req *dns.Msg) string {
	q := req.Question[0]
	do := "0"
	if opt := req.IsEdns0(); opt != nil && opt.Do() {
		do = "1"
	}
	return strings.ToLower(q.Name) + "|" + strconv.Itoa(int(q.Qtype)) + "|" + strconv.Itoa(int(q.Qclass)) + "|" + do
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

func (c *cache) put(key string, msg *dns.Msg, minTTL, maxTTL int) {
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
	if !seen {
		ttl = 30
	}
	if len(msg.Answer) == 0 && ttl > 300 {
		ttl = 300 // negative answers
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
	c.mu.Lock()
	defer c.mu.Unlock()
	if len(c.entries) >= cacheMaxEntries {
		drop := cacheMaxEntries / 10
		for k := range c.entries {
			delete(c.entries, k)
			if drop--; drop <= 0 {
				break
			}
		}
	}
	c.entries[key] = cacheEntry{msg: msg.Copy(), stored: time.Now(), ttl: time.Duration(ttl) * time.Second}
}

func (c *cache) flush() {
	c.mu.Lock()
	c.entries = map[string]cacheEntry{}
	c.mu.Unlock()
}

func (c *cache) size() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return len(c.entries)
}
