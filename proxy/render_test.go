package main

import (
	"strings"
	"testing"
)

// Verifies the action field flows from the allowlist JSON into the rendered ACL, that the
// secure-by-default fallbacks hold, and that the default block stays enforce (deny-all when
// no fallback allowlist is supplied).
func TestRenderActions(t *testing.T) {
	mods := []module{
		{ID: "rep", Subnet: "172.30.10.0/24", AllowedHosts: []string{"a.com"}, Action: "report"},
		{ID: "enf", Subnet: "172.30.20.0/24", AllowedHosts: []string{"b.com"}, Action: "enforce"},
		{ID: "opn", Subnet: "172.30.30.0/24", AllowedHosts: []string{"c.com"}, Action: "open"},
		{ID: "omit", Subnet: "172.30.40.0/24", AllowedHosts: []string{"d.com"}},                     // omitted -> enforce
		{ID: "typo", Subnet: "172.30.50.0/24", AllowedHosts: []string{"e.com"}, Action: "reprot"},   // typo -> enforce
		{ID: "case", Subnet: "172.30.60.0/24", AllowedHosts: []string{"f.com"}, Action: " Report "}, // trim+lower -> report
	}
	got := renderSmokescreenACL("netid", mods, nil)

	want := map[string]string{
		"rep":  "  - name: rep\n    project: egress\n    action: report\n",
		"enf":  "  - name: enf\n    project: egress\n    action: enforce\n",
		"opn":  "  - name: opn\n    project: egress\n    action: open\n",
		"omit": "  - name: omit\n    project: egress\n    action: enforce\n",
		"typo": "  - name: typo\n    project: egress\n    action: enforce\n",
		"case": "  - name: case\n    project: egress\n    action: report\n",
	}
	for id, frag := range want {
		if !strings.Contains(got, frag) {
			t.Errorf("module %s: rendered ACL missing %q\n--- got ---\n%s", id, frag, got)
		}
	}
	// No fallback => default block is deny-all enforce.
	if !strings.Contains(got, "default:\n  name: default\n  action: enforce\n  allowed_domains: []\n") {
		t.Errorf("default block not deny-all enforce\n--- got ---\n%s", got)
	}
}

// In the token-identity modes the role is the workload's appid (managed-identity client
// ID), so the ACL must key on modules[].appid; in the other modes it keys on the id. A
// jwt-mode module without an appid can never match a role — it renders under its id
// (harmless) rather than being dropped.
func TestRenderServiceNameByMode(t *testing.T) {
	mods := []module{
		{ID: "sample-app", Appid: "11111111-2222-3333-4444-555555555555", AllowedHosts: []string{"api.example.com"}},
		{ID: "no-appid", AllowedHosts: []string{"b.example.com"}},
	}

	for _, mode := range []string{"basic-jwt", "jwt"} {
		got := renderSmokescreenACL(mode, mods, nil)
		if !strings.Contains(got, "  - name: 11111111-2222-3333-4444-555555555555\n") {
			t.Errorf("%s: ACL not keyed on appid\n--- got ---\n%s", mode, got)
		}
		if strings.Contains(got, "  - name: sample-app\n") {
			t.Errorf("%s: module with appid must not also render under its id\n--- got ---\n%s", mode, got)
		}
		if !strings.Contains(got, "  - name: no-appid\n") {
			t.Errorf("%s: appid-less module should fall back to id\n--- got ---\n%s", mode, got)
		}
	}

	for _, mode := range []string{"netid", "basic-name", ""} {
		got := renderSmokescreenACL(mode, mods, nil)
		if !strings.Contains(got, "  - name: sample-app\n") {
			t.Errorf("%s: ACL should key on id\n--- got ---\n%s", mode, got)
		}
		if strings.Contains(got, "11111111-2222-3333-4444-555555555555") {
			t.Errorf("%s: appid must not be the ACL key in this mode\n--- got ---\n%s", mode, got)
		}
	}
}

// The optional fallback widens the default block to a curated baseline allowlist, but the
// default block stays in ENFORCE mode (a permissive default is never rendered).
func TestRenderFallback(t *testing.T) {
	fb := &fallback{AllowedHosts: []string{"baseline.example.com", "shared.example.com"}}
	got := renderSmokescreenACL("netid", nil, fb)

	wantDefault := "default:\n  name: default\n  action: enforce\n  allowed_domains:\n" +
		"    - baseline.example.com\n    - shared.example.com\n"
	if !strings.Contains(got, wantDefault) {
		t.Errorf("fallback not rendered into enforce default block\n--- got ---\n%s", got)
	}

	// An empty fallback is treated like no fallback: deny-all.
	if g := renderSmokescreenACL("netid", nil, &fallback{}); !strings.Contains(g, "  allowed_domains: []\n") {
		t.Errorf("empty fallback should render deny-all default\n--- got ---\n%s", g)
	}
}

func TestNormalizeAction(t *testing.T) {
	cases := map[string]string{
		"report": "report", "open": "open", "enforce": "enforce",
		"": "enforce", "ENFORCE": "enforce", "Report": "report",
		"  open  ": "open", "bogus": "enforce", "deny": "enforce",
	}
	for in, want := range cases {
		if got := normalizeAction("m", in); got != want {
			t.Errorf("normalizeAction(%q) = %q, want %q", in, got, want)
		}
	}
}
