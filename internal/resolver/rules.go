package resolver

import (
	"strings"

	"naxdns/internal/config"
)

const singleLabel = "<single-label>"

// ruleSet matches a query name against the configured rules; the earliest
// rule in configuration order wins.
type ruleSet struct {
	rules       []config.Rule
	withSelf    map[string]int // "example.com": the domain and its subdomains
	subOnly     map[string]int // "*.example.com": subdomains only
	singleLabel int
}

func compileRules(rules []config.Rule) *ruleSet {
	rs := &ruleSet{rules: rules, withSelf: map[string]int{}, subOnly: map[string]int{}, singleLabel: -1}
	add := func(m map[string]int, k string, i int) {
		if _, ok := m[k]; !ok {
			m[k] = i
		}
	}
	for i, r := range rules {
		if !r.Enabled {
			continue
		}
		for _, d := range r.Domains {
			d = strings.ToLower(strings.TrimSuffix(strings.TrimSpace(d), "."))
			switch {
			case d == singleLabel:
				if rs.singleLabel < 0 {
					rs.singleLabel = i
				}
			case strings.HasPrefix(d, "*."):
				add(rs.subOnly, d[2:], i)
			case d != "":
				add(rs.withSelf, d, i)
			}
		}
	}
	return rs
}

// match returns the winning rule for a lower-case name without trailing dot.
func (rs *ruleSet) match(name string) *config.Rule {
	best := -1
	consider := func(i int, ok bool) {
		if ok && (best < 0 || i < best) {
			best = i
		}
	}
	if name != "" && !strings.Contains(name, ".") {
		consider(rs.singleLabel, rs.singleLabel >= 0)
	}
	i, ok := rs.withSelf[name]
	consider(i, ok)
	for rest := name; ; {
		dot := strings.IndexByte(rest, '.')
		if dot < 0 {
			break
		}
		rest = rest[dot+1:]
		i, ok = rs.withSelf[rest]
		consider(i, ok)
		i, ok = rs.subOnly[rest]
		consider(i, ok)
	}
	if best < 0 {
		return nil
	}
	return &rs.rules[best]
}
