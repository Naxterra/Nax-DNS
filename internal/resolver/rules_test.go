package resolver

import (
	"testing"

	"naxdns/internal/config"
)

func TestRuleMatching(t *testing.T) {
	rs := compileRules([]config.Rule{
		{Name: "sub-only", Enabled: true, Domains: []string{"*.corp.example"}},
		{Name: "with-self", Enabled: true, Domains: []string{"Example.COM.", "<single-label>"}},
		{Name: "disabled", Enabled: true, Domains: []string{"www.example.com"}},
		{Name: "off", Enabled: false, Domains: []string{"off.test"}},
	})
	cases := map[string]string{
		"example.com":         "with-self",
		"www.example.com":     "with-self", // earlier rule wins over the more specific later one
		"a.b.example.com":     "with-self",
		"notexample.com":      "",
		"corp.example":        "",
		"vpn.corp.example":    "sub-only",
		"printer":             "with-self",
		"off.test":            "",
		"example.com.evil.io": "",
	}
	for name, want := range cases {
		got := ""
		if r := rs.match(name); r != nil {
			got = r.Name
		}
		if got != want {
			t.Errorf("match(%q) = %q, want %q", name, got, want)
		}
	}
}
