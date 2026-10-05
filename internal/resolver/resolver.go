// Package resolver decides what happens to each DNS query: built-in answers,
// rules, cache, and the encrypted server chain with failover.
package resolver

import (
	"context"
	"errors"
	"fmt"
	"net/netip"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/miekg/dns"

	"naxdns/internal/config"
	"naxdns/internal/upstream"
)

const (
	maxCooldown   = time.Minute
	probeInterval = 2 * time.Second
	probeName     = "example.com."
	logSize       = 3000
)

// Origin describes where a query came from.
type Origin struct {
	Proto  string         // "udp", "tcp" or "test"
	Server netip.AddrPort // DNS server the application addressed; zero if unknown
}

// Result is either a reply or the instruction to let the original query
// continue to the server it was addressed to.
type Result struct {
	Msg         *dns.Msg
	Passthrough bool
}

// Entry is one line of the query log.
type Entry struct {
	Seq      uint64    `json:"seq"`
	Time     time.Time `json:"time"`
	Name     string    `json:"name"`
	Type     string    `json:"type"`
	Proto    string    `json:"proto"`
	Dest     string    `json:"dest"`
	Action   string    `json:"action"` // resolved, cached, stale, blocked, passthrough, failed
	Server   string    `json:"server"`
	Protocol string    `json:"protocol"`
	Rule     string    `json:"rule"`
	Rcode    string    `json:"rcode"`
	Answer   string    `json:"answer"`
	Ms       float64   `json:"ms"`
	Detail   string    `json:"detail"`
}

// ServerState is the live health of one configured server.
type ServerState struct {
	ID        string    `json:"id"`
	Healthy   bool      `json:"healthy"`
	Fails     int       `json:"fails"`
	LastError string    `json:"lastError"`
	LastMs    float64   `json:"lastMs"`
	AvgMs     float64   `json:"avgMs"`
	Queries   uint64    `json:"queries"`
	Failures  uint64    `json:"failures"`
	LastOK    time.Time `json:"lastOk"`
}

type Stats struct {
	Total       uint64 `json:"total"`
	Resolved    uint64 `json:"resolved"`
	Cached      uint64 `json:"cached"`
	Blocked     uint64 `json:"blocked"`
	Passthrough uint64 `json:"passthrough"`
	Failed      uint64 `json:"failed"`
	CacheSize   int    `json:"cacheSize"`
}

type server struct {
	cfg    config.Upstream
	client upstream.Server
	proto  string

	mu        sync.Mutex
	down      bool
	fails     int
	cooldown  time.Duration
	nextProbe time.Time
	probing   bool
	lastErr   string
	lastMs    float64
	avgMs     float64
	queries   uint64
	failures  uint64
	lastOK    time.Time
}

// state is an immutable snapshot swapped on every configuration change.
type state struct {
	settings config.Settings
	servers  []*server // enabled servers in priority order
	byID     map[string]*server
	rules    *ruleSet
	boot     *upstream.Bootstrap
}

type Resolver struct {
	st    atomic.Pointer[state]
	cache *cache

	total, resolved, cached, blocked, passthrough, failed atomic.Uint64

	logMu  sync.Mutex
	log    []Entry
	logSeq uint64

	stop chan struct{}
}

func New() *Resolver {
	r := &Resolver{cache: newCache(), stop: make(chan struct{})}
	r.st.Store(&state{rules: compileRules(nil), byID: map[string]*server{}})
	go r.probeLoop()
	return r
}

func (r *Resolver) Close() {
	close(r.stop)
	closeState(r.st.Load())
}

func closeState(s *state) {
	for _, srv := range s.byID {
		srv.client.Close()
	}
	if s.boot != nil {
		s.boot.Close()
	}
}

// Reload replaces servers and rules. Health starts fresh.
func (r *Resolver) Reload(cfg *config.Config) error {
	timeout := time.Duration(cfg.Settings.TimeoutMs) * time.Millisecond
	boot, err := upstream.NewBootstrap(cfg.Settings.BootstrapDoH, timeout)
	if err != nil {
		return err
	}
	ns := &state{settings: cfg.Settings, rules: compileRules(cfg.Rules), byID: map[string]*server{}, boot: boot}
	for _, u := range cfg.Upstreams {
		if !u.Enabled {
			continue
		}
		client, err := upstream.New(u, boot, timeout)
		if err != nil {
			closeState(ns)
			return fmt.Errorf("server %q: %w", u.Name, err)
		}
		srv := &server{cfg: u, client: client, proto: upstream.Protocol(u.URL)}
		ns.servers = append(ns.servers, srv)
		ns.byID[u.ID] = srv
	}
	old := r.st.Swap(ns)
	r.cache.flush()
	// Let in-flight queries finish on the old clients before closing them.
	time.AfterFunc(10*time.Second, func() { closeState(old) })
	return nil
}

func (r *Resolver) FlushCache() { r.cache.flush() }

func (r *Resolver) Stats() Stats {
	return Stats{
		Total: r.total.Load(), Resolved: r.resolved.Load(), Cached: r.cached.Load(),
		Blocked: r.blocked.Load(), Passthrough: r.passthrough.Load(), Failed: r.failed.Load(),
		CacheSize: r.cache.size(),
	}
}

func (r *Resolver) ServerStates() []ServerState {
	var out []ServerState
	for _, s := range r.st.Load().servers {
		s.mu.Lock()
		out = append(out, ServerState{
			ID: s.cfg.ID, Healthy: !s.down, Fails: s.fails, LastError: s.lastErr,
			LastMs: s.lastMs, AvgMs: s.avgMs, Queries: s.queries, Failures: s.failures, LastOK: s.lastOK,
		})
		s.mu.Unlock()
	}
	return out
}

// LogSince returns log entries newer than seq.
func (r *Resolver) LogSince(seq uint64) []Entry {
	r.logMu.Lock()
	defer r.logMu.Unlock()
	if seq > r.logSeq {
		seq = 0 // the caller remembers a previous run of the service
	}
	i := len(r.log)
	for i > 0 && r.log[i-1].Seq > seq {
		i--
	}
	return append([]Entry{}, r.log[i:]...)
}

func (r *Resolver) record(e Entry, start time.Time) {
	e.Time = start
	e.Ms = float64(time.Since(start).Microseconds()) / 1000
	r.total.Add(1)
	switch e.Action {
	case "resolved":
		r.resolved.Add(1)
	case "cached", "stale":
		r.cached.Add(1)
	case "blocked":
		r.blocked.Add(1)
	case "passthrough":
		r.passthrough.Add(1)
	case "failed":
		r.failed.Add(1)
	}
	r.logMu.Lock()
	r.logSeq++
	e.Seq = r.logSeq
	r.log = append(r.log, e)
	if len(r.log) > logSize {
		r.log = append(r.log[:0], r.log[len(r.log)-logSize*3/4:]...)
	}
	r.logMu.Unlock()
}

func reply(req *dns.Msg, rcode int) *dns.Msg {
	m := new(dns.Msg)
	m.SetRcode(req, rcode)
	m.RecursionAvailable = true
	return m
}

// finish adapts an upstream or cached message to the client's request.
func finish(req, resp *dns.Msg) *dns.Msg {
	resp.Id = req.Id
	resp.Question = req.Question // keep the client's 0x20 casing
	return resp
}

func summarize(m *dns.Msg) string {
	var parts []string
	for _, rr := range m.Answer {
		switch v := rr.(type) {
		case *dns.A:
			parts = append(parts, v.A.String())
		case *dns.AAAA:
			parts = append(parts, v.AAAA.String())
		case *dns.CNAME:
			parts = append(parts, "→ "+strings.TrimSuffix(v.Target, "."))
		default:
			parts = append(parts, dns.TypeToString[rr.Header().Rrtype])
		}
		if len(parts) == 4 {
			parts = append(parts, "…")
			break
		}
	}
	return strings.Join(parts, ", ")
}

// Resolve handles one query.
func (r *Resolver) Resolve(ctx context.Context, req *dns.Msg, o Origin) Result {
	start := time.Now()
	if len(req.Question) != 1 {
		return Result{Msg: reply(req, dns.RcodeFormatError)}
	}
	st := r.st.Load()
	q := req.Question[0]
	name := strings.ToLower(strings.TrimSuffix(q.Name, "."))
	e := Entry{Name: name, Type: dns.TypeToString[q.Qtype], Proto: o.Proto}
	if e.Type == "" {
		e.Type = fmt.Sprintf("TYPE%d", q.Qtype)
	}
	if o.Server.IsValid() {
		e.Dest = o.Server.Addr().Unmap().String()
	}
	block := func(why string) Result {
		e.Action, e.Rule, e.Rcode = "blocked", why, "NXDOMAIN"
		r.record(e, start)
		return Result{Msg: reply(req, dns.RcodeNameError)}
	}
	pass := func() Result {
		if o.Proto == "udp" {
			e.Action = "passthrough"
			r.record(e, start)
			return Result{Passthrough: true}
		}
		// No original packet to release: forward a copy to the original server.
		if o.Server.IsValid() {
			if resp, err := upstream.PlainExchange(ctx, o.Server, req); err == nil {
				e.Action, e.Rcode, e.Answer = "passthrough", dns.RcodeToString[resp.Rcode], summarize(resp)
				r.record(e, start)
				return Result{Msg: resp}
			}
		}
		e.Action, e.Rcode = "failed", "SERVFAIL"
		if o.Proto == "test" {
			e.Action, e.Rcode, e.Detail = "passthrough", "", "would be answered by the original DNS server"
		}
		r.record(e, start)
		return Result{Msg: reply(req, dns.RcodeServerFailure)}
	}

	// Stop applications and Windows from discovering their own encrypted
	// resolver, which would bypass port 53 and therefore this service.
	if st.settings.BlockDDR && (name == "resolver.arpa" || strings.HasSuffix(name, ".resolver.arpa")) {
		return block("no encrypted-DNS discovery")
	}
	if st.settings.FirefoxCanary && name == "use-application-dns.net" {
		return block("Firefox DoH canary")
	}

	candidates := st.servers
	if rule := st.rules.match(name); rule != nil {
		e.Rule = rule.Name
		switch rule.Action {
		case config.ActionBlock:
			return block(rule.Name)
		case config.ActionPassthrough:
			return pass()
		case config.ActionUpstream:
			if first := st.byID[rule.Upstream]; first != nil {
				candidates = []*server{first}
				for _, s := range st.servers {
					if s != first {
						candidates = append(candidates, s)
					}
				}
			}
		}
	}

	key := cacheKey(req)
	var stale *dns.Msg
	if st.settings.Cache {
		if msg, isStale, ok := r.cache.get(key); ok {
			if !isStale {
				e.Action, e.Rcode, e.Answer = "cached", dns.RcodeToString[msg.Rcode], summarize(msg)
				r.record(e, start)
				return Result{Msg: finish(req, msg)}
			}
			stale = msg
		}
	}

	resp, srv, detail := r.exchange(ctx, st, candidates, req)
	e.Detail = detail
	if resp != nil {
		if st.settings.Cache {
			r.cache.put(key, resp, st.settings.CacheMinTTL, st.settings.CacheMaxTTL)
		}
		e.Action, e.Server, e.Protocol = "resolved", srv.cfg.Name, srv.proto
		e.Rcode, e.Answer = dns.RcodeToString[resp.Rcode], summarize(resp)
		r.record(e, start)
		return Result{Msg: finish(req, resp)}
	}
	if stale != nil {
		e.Action, e.Rcode, e.Answer = "stale", dns.RcodeToString[stale.Rcode], summarize(stale)
		r.record(e, start)
		return Result{Msg: finish(req, stale)}
	}
	if st.settings.OnFailure == config.FailPassthrough && o.Proto != "test" {
		return pass()
	}
	e.Action, e.Rcode = "failed", "SERVFAIL"
	r.record(e, start)
	return Result{Msg: reply(req, dns.RcodeServerFailure)}
}

type attempt struct {
	srv  *server
	resp *dns.Msg
	err  error
}

// exchange tries servers in order. A failed server is followed immediately by
// the next one; a slow server is raced against the next after the hedge delay.
// Servers marked down are skipped while a healthy one exists.
func (r *Resolver) exchange(ctx context.Context, st *state, candidates []*server, req *dns.Msg) (*dns.Msg, *server, string) {
	var order, down []*server
	for _, s := range candidates {
		s.mu.Lock()
		isDown := s.down
		s.mu.Unlock()
		if isDown {
			down = append(down, s)
		} else {
			order = append(order, s)
		}
	}
	if len(order) == 0 {
		if st.settings.OnFailure == config.FailPassthrough || len(down) == 0 {
			return nil, nil, "all servers are down"
		}
		order = down // strict mode: keep trying rather than give up
	}

	timeout := time.Duration(st.settings.TimeoutMs) * time.Millisecond
	ctx, cancel := context.WithTimeout(ctx, timeout+time.Duration(len(order))*time.Duration(st.settings.HedgeMs)*time.Millisecond)
	defer cancel()

	results := make(chan attempt, len(order))
	launch := func(s *server) {
		go func() {
			began := time.Now()
			actx, acancel := context.WithTimeout(ctx, timeout)
			defer acancel()
			resp, err := s.client.Exchange(actx, req.Copy())
			if err == nil && resp == nil {
				err = errors.New("empty response")
			}
			// Losing a race is not a server fault.
			if !(err != nil && ctx.Err() != nil && actx.Err() == context.Canceled) {
				s.observe(st, err, time.Since(began))
			}
			results <- attempt{s, resp, err}
		}()
	}

	hedgeDelay := time.Duration(st.settings.HedgeMs) * time.Millisecond
	launched := make([]time.Time, len(order))
	answered := map[*server]bool{}
	// A server that stayed silent past the hedge delay while another one
	// answered counts as failed, so a dead primary is skipped after a few
	// queries instead of delaying every one of them.
	penalizeSilent := func(winner *server) {
		for i, s := range order {
			if s != winner && !answered[s] && !launched[i].IsZero() && time.Since(launched[i]) >= hedgeDelay {
				s.observe(st, errors.New("no answer before the backup server replied"), 0)
			}
		}
	}

	next, pending := 0, 0
	start := func() bool {
		if next >= len(order) {
			return false
		}
		launched[next] = time.Now()
		launch(order[next])
		next++
		pending++
		return true
	}
	start()
	hedge := time.NewTimer(hedgeDelay)
	defer hedge.Stop()

	var errs []string
	var servfail *attempt
	for pending > 0 {
		select {
		case a := <-results:
			pending--
			answered[a.srv] = true
			switch {
			case a.err != nil:
				errs = append(errs, a.srv.cfg.Name+": "+a.err.Error())
			case a.resp.Rcode == dns.RcodeServerFailure:
				servfail = &a
				errs = append(errs, a.srv.cfg.Name+": SERVFAIL")
			default:
				penalizeSilent(a.srv)
				return a.resp, a.srv, strings.Join(errs, "; ")
			}
			start()
		case <-hedge.C:
			if start() {
				hedge.Reset(hedgeDelay)
			}
		case <-ctx.Done():
			pending = 0
			errs = append(errs, "timed out")
		}
	}
	if servfail != nil {
		return servfail.resp, servfail.srv, strings.Join(errs, "; ")
	}
	return nil, nil, strings.Join(errs, "; ")
}

func (s *server) observe(st *state, err error, took time.Duration) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.queries++
	if err == nil {
		ms := float64(took.Microseconds()) / 1000
		s.lastMs = ms
		if s.avgMs == 0 {
			s.avgMs = ms
		} else {
			s.avgMs = s.avgMs*0.9 + ms*0.1
		}
		s.fails, s.down, s.cooldown, s.lastErr, s.lastOK = 0, false, 0, "", time.Now()
		return
	}
	s.failures++
	s.fails++
	s.lastErr = err.Error()
	if s.down {
		if time.Now().After(s.nextProbe) {
			s.cooldown = min(s.cooldown*2, maxCooldown)
			s.nextProbe = time.Now().Add(s.cooldown)
		}
	} else if s.fails >= st.settings.FailThreshold {
		s.down = true
		s.cooldown = time.Duration(st.settings.CooldownSec) * time.Second
		s.nextProbe = time.Now().Add(s.cooldown)
	}
}

// probeLoop brings servers back as soon as they answer again.
func (r *Resolver) probeLoop() {
	t := time.NewTicker(probeInterval)
	defer t.Stop()
	for {
		select {
		case <-r.stop:
			return
		case <-t.C:
		}
		st := r.st.Load()
		// With nothing healthy (typically a VPN connecting or the network
		// changing), check every tick instead of waiting out the cooldowns.
		anyHealthy := false
		for _, s := range st.servers {
			s.mu.Lock()
			anyHealthy = anyHealthy || !s.down
			s.mu.Unlock()
		}
		for _, s := range st.servers {
			s.mu.Lock()
			due := s.down && !s.probing && (!anyHealthy || time.Now().After(s.nextProbe))
			if due {
				s.probing = true
			}
			s.mu.Unlock()
			if !due {
				continue
			}
			go func() {
				began := time.Now()
				ctx, cancel := context.WithTimeout(context.Background(), time.Duration(st.settings.TimeoutMs)*time.Millisecond)
				defer cancel()
				req := new(dns.Msg)
				req.SetQuestion(probeName, dns.TypeA)
				resp, err := s.client.Exchange(ctx, req)
				if err == nil && resp == nil {
					err = errors.New("empty response")
				}
				s.observe(st, err, time.Since(began))
				s.mu.Lock()
				s.probing = false
				s.mu.Unlock()
			}()
		}
	}
}

// TestResult is the outcome of a manual server test.
type TestResult struct {
	ID        string  `json:"id"`
	OK        bool    `json:"ok"`
	ConnectMs float64 `json:"connectMs"` // first query, includes the handshake
	QueryMs   float64 `json:"queryMs"`   // median of warm queries
	Error     string  `json:"error"`
}

// Test measures a server with a fresh connection, independent of live routing.
func Test(ctx context.Context, u config.Upstream, s config.Settings) TestResult {
	res := TestResult{ID: u.ID}
	timeout := time.Duration(s.TimeoutMs) * time.Millisecond
	boot, err := upstream.NewBootstrap(s.BootstrapDoH, timeout)
	if err != nil {
		res.Error = err.Error()
		return res
	}
	defer boot.Close()
	client, err := upstream.New(u, boot, timeout)
	if err != nil {
		res.Error = err.Error()
		return res
	}
	defer client.Close()
	once := func(name string) (float64, error) {
		req := new(dns.Msg)
		req.SetQuestion(name, dns.TypeA)
		began := time.Now()
		qctx, cancel := context.WithTimeout(ctx, timeout)
		defer cancel()
		resp, err := client.Exchange(qctx, req)
		if err == nil && resp == nil {
			err = errors.New("empty response")
		}
		return float64(time.Since(began).Microseconds()) / 1000, err
	}
	if res.ConnectMs, err = once(probeName); err != nil {
		res.Error = err.Error()
		return res
	}
	var warm []float64
	for _, n := range []string{"example.org.", "example.net.", probeName} {
		ms, err := once(n)
		if err != nil {
			res.Error = err.Error()
			return res
		}
		warm = append(warm, ms)
	}
	// median of three
	a, b, c := warm[0], warm[1], warm[2]
	res.QueryMs = max(min(a, b), min(max(a, b), c))
	res.OK = true
	return res
}
