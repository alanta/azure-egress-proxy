package main

import (
	"bufio"
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/sirupsen/logrus"
	"github.com/stripe/smokescreen/cmd"
	"github.com/stripe/smokescreen/pkg/smokescreen"
	acl "github.com/stripe/smokescreen/pkg/smokescreen/acl/v1"
	"github.com/stripe/smokescreen/pkg/smokescreen/metrics"
)

// These tests pin the contracts this repo relies on from the embedded Smokescreen, so a
// dependency bump that changes a default or an API fails here rather than in production.
// They assert policy and audit behaviour, never upstream wording.

// contractConf builds a configuration the way main and runManaged do: cmd.NewConfiguration
// from a config file, then the repo's own settings. extraYAML is appended to the config, so a
// test can add a `connect_timeout` or the deployed deny_ranges.
func contractConf(t *testing.T, aclYAML, extraYAML string) *smokescreen.Config {
	t.Helper()
	dir := t.TempDir()
	aclPath := filepath.Join(dir, "acl.yaml")
	if err := os.WriteFile(aclPath, []byte(aclYAML), 0o644); err != nil {
		t.Fatal(err)
	}
	// Mirrors infra/assets/cloud-init.yaml, minus ip/port (the tests serve via httptest).
	cfg := "acl_file: " + aclPath + "\nallow_missing_role: false\n" + extraYAML
	cfgPath := filepath.Join(dir, "config.yaml")
	if err := os.WriteFile(cfgPath, []byte(cfg), 0o644); err != nil {
		t.Fatal(err)
	}
	conf, err := cmd.NewConfiguration([]string{"egress-proxy", "--config-file", cfgPath}, nil)
	if err != nil || conf == nil {
		t.Fatalf("NewConfiguration: conf=%v err=%v", conf, err)
	}
	conf.MetricsClient = metrics.NewNoOpMetricsClient()
	return conf
}

// The outbound connect timeout defaults to 10 s (Smokescreen's DefaultConnectTimeout since
// v0.1.0; earlier pins had no bound) and stays settable per deployment with `connect_timeout`
// in the config file, so an operator can tune it without a rebuild. An explicit 0 disables
// it; that is upstream's contract and an operator's choice, not a default.
func TestConnectTimeoutDefaultAndOverride(t *testing.T) {
	for name, c := range map[string]struct {
		extra string
		want  time.Duration
	}{
		"omitted in config":    {"", 10 * time.Second},
		"set in config":        {"connect_timeout: 45s\n", 45 * time.Second},
		"explicit 0 in config": {"connect_timeout: 0s\n", 0},
	} {
		t.Run(name, func(t *testing.T) {
			conf := contractConf(t, renderSmokescreenACL("netid", nil, nil), c.extra)
			if conf.ConnectTimeout != c.want {
				t.Errorf("ConnectTimeout = %s, want %s", conf.ConnectTimeout, c.want)
			}
		})
	}
}

// The 300 s read/idle behaviour docs/production-hardening.md relies on: with no idle_timeout
// configured, Smokescreen's http.Server leaves IdleTimeout unset, so net/http falls back to
// ReadTimeout, which defaults to 300 s. If an upstream bump changes either side, the
// stale-tunnel contract for clients changes with it.
func TestHTTPReadIdleDefaultsStay300s(t *testing.T) {
	conf := contractConf(t, renderSmokescreenACL("netid", nil, nil), "")
	if conf.IdleTimeout != 0 {
		t.Errorf("IdleTimeout = %s, want 0 (unset, so http.Server falls back to ReadTimeout)", conf.IdleTimeout)
	}
	for name, got := range map[string]time.Duration{
		"ReadTimeout": conf.ReadTimeout, "ReadHeaderTimeout": conf.ReadHeaderTimeout, "WriteTimeout": conf.WriteTimeout,
	} {
		if got != 300*time.Second {
			t.Errorf("%s = %s, want 300s", name, got)
		}
	}
}

// action modes, appid matching, fallback and empty-services behaviour through the real
// ACL decider, for the ACL exactly as rendered.
func TestACLDecisionSemantics(t *testing.T) {
	const appid = "11111111-2222-3333-4444-555555555555"
	doc := allowlistDoc{
		Modules: []module{
			{ID: "enf", Appid: appid, AllowedHosts: []string{"a.example"}, Action: "enforce"},
			{ID: "rep", Appid: "rep-app", AllowedHosts: []string{"r.example"}, Action: "report"},
			{ID: "opn", Appid: "opn-app", AllowedHosts: []string{"o.example"}, Action: "open"},
			{ID: "omit", Appid: "omit-app", AllowedHosts: []string{"m.example"}},
		},
		Fallback: &fallback{AllowedHosts: []string{"baseline.example"}},
	}
	path := filepath.Join(t.TempDir(), "acl.yaml")
	if err := os.WriteFile(path, []byte(renderSmokescreenACL("basic-jwt", doc.Modules, doc.Fallback)), 0o644); err != nil {
		t.Fatal(err)
	}
	a, err := acl.New(logrus.New(), acl.NewYAMLLoader(path), nil)
	if err != nil {
		t.Fatal(err)
	}

	cases := []struct {
		name, svc, host string
		want            acl.DecisionResult
	}{
		{"enforce allows listed host", appid, "a.example", acl.Allow},
		{"enforce denies unlisted host", appid, "evil.example", acl.Deny},
		{"enforce matches the appid, not the module id", "enf", "a.example", acl.Deny},
		{"enforce does not borrow another service's host", appid, "r.example", acl.Deny},
		{"report allows listed host", "rep-app", "r.example", acl.Allow},
		{"report lets unlisted host through (would-deny only)", "rep-app", "evil.example", acl.AllowAndReport},
		{"open allows anything", "opn-app", "evil.example", acl.Allow},
		{"omitted action enforces", "omit-app", "evil.example", acl.Deny},
		{"unknown service gets the fallback allowlist", "stranger", "baseline.example", acl.Allow},
		{"unknown service is denied elsewhere", "stranger", "evil.example", acl.Deny},
		{"fallback is not a widening of a known service", appid, "baseline.example", acl.Deny},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			d, err := a.Decide(acl.DecideArgs{Service: c.svc, Host: c.host})
			if err != nil {
				t.Fatal(err)
			}
			got := d.Result
			// A report-mode miss is expressed as an allow flagged "would deny"; either way it
			// must not be a Deny, and an enforce miss must be.
			if c.want == acl.AllowAndReport {
				if got == acl.Deny {
					t.Errorf("got deny, want allow-with-report")
				}
				return
			}
			if got != c.want {
				t.Errorf("%s -> %s: got %v, want %v", c.svc, c.host, got, c.want)
			}
		})
	}
}

// proxyHarness serves the real Smokescreen handler, configured and wired as the proxy wires
// it, and captures the audit log it would ship.
type proxyHarness struct {
	addr string
	logs *bytes.Buffer
}

func startProxy(t *testing.T, mode, aclYAML, extraYAML string) *proxyHarness {
	t.Helper()
	conf := contractConf(t, aclYAML, extraYAML)
	logs := &bytes.Buffer{}
	conf.Log.Out = logs
	conf.Log.Level = logrus.DebugLevel
	conf.RoleFromRequest = roleFromRequest(mode)
	conf.RejectResponseHandlerWithCtx = newRejectHandler(mode)
	applyJSONLogging(conf)

	srv := httptest.NewServer(smokescreen.BuildProxy(conf))
	t.Cleanup(srv.Close)
	return &proxyHarness{addr: strings.TrimPrefix(srv.URL, "http://"), logs: logs}
}

// connect sends one CONNECT and returns the response status line and headers.
func (p *proxyHarness) connect(t *testing.T, target, authHeader string) *http.Response {
	t.Helper()
	c, err := net.DialTimeout("tcp", p.addr, 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { c.Close() })
	_ = c.SetDeadline(time.Now().Add(5 * time.Second))
	req := fmt.Sprintf("CONNECT %s HTTP/1.1\r\nHost: %s\r\n", target, target)
	if authHeader != "" {
		req += "Proxy-Authorization: " + authHeader + "\r\n"
	}
	if _, err := c.Write([]byte(req + "\r\n")); err != nil {
		t.Fatal(err)
	}
	resp, err := http.ReadResponse(bufio.NewReader(c), &http.Request{Method: "CONNECT"})
	if err != nil {
		t.Fatal(err)
	}
	return resp
}

// decisionRows returns the audit rows whose message is msg, as parsed JSON.
func (p *proxyHarness) rows(t *testing.T, msg string) []map[string]any {
	t.Helper()
	var out []map[string]any
	for _, line := range bytes.Split(p.logs.Bytes(), []byte("\n")) {
		if len(bytes.TrimSpace(line)) == 0 {
			continue
		}
		var row map[string]any
		if err := json.Unmarshal(line, &row); err != nil {
			t.Fatalf("audit line is not JSON: %q", line)
		}
		if row["msg"] == msg {
			out = append(out, row)
		}
	}
	return out
}

func basicHeader(user, pass string) string {
	return "Basic " + base64.StdEncoding.EncodeToString([]byte(user+":"+pass))
}

// The 407 handshake end to end through the real handler: a credential-less CONNECT is
// challenged and audited as CANONICAL-PROXY-AUTH-REQUIRED (never dropped, never a decision
// denial), while a CONNECT whose credential is rejected is a 407 *without* a challenge and
// stays a CANONICAL-PROXY-DECISION denial that names the real cause. The split keys only on
// whether Proxy-Authorization was sent.
func TestBasicHandshakeAuditClassification(t *testing.T) {
	p := startProxy(t, "basic-name", renderSmokescreenACL("basic-name", []module{
		{ID: "svc", AllowedHosts: []string{"a.example"}},
	}, nil), "")

	// 1. credential-less
	resp := p.connect(t, "a.example:443", "")
	if resp.StatusCode != http.StatusProxyAuthRequired {
		t.Fatalf("bare CONNECT: status %d, want 407", resp.StatusCode)
	}
	if !strings.HasPrefix(resp.Header.Get("Proxy-Authenticate"), "Basic") {
		t.Errorf("bare CONNECT: Proxy-Authenticate = %q, want a Basic challenge", resp.Header.Get("Proxy-Authenticate"))
	}

	// 2. credential present but unusable (empty username)
	resp = p.connect(t, "a.example:443", basicHeader("", "x"))
	if resp.StatusCode != http.StatusProxyAuthRequired {
		t.Fatalf("rejected credential: status %d, want 407", resp.StatusCode)
	}
	if resp.Header.Get("Proxy-Authenticate") != "" {
		t.Errorf("rejected credential must not be re-challenged, got %q", resp.Header.Get("Proxy-Authenticate"))
	}

	// 3. valid identity, host not allowed: an ordinary policy denial
	resp = p.connect(t, "evil.example:443", basicHeader("svc", "x"))
	if resp.StatusCode == http.StatusOK {
		t.Fatalf("unlisted host: tunnel established")
	}

	required := p.rows(t, canonicalProxyAuthRequired)
	if len(required) != 1 {
		t.Fatalf("got %d %s rows, want exactly 1 (the bare CONNECT)\n%s", len(required), canonicalProxyAuthRequired, p.logs)
	}
	decisions := p.rows(t, smokescreen.CanonicalProxyDecision)
	if len(decisions) != 2 {
		t.Fatalf("got %d %s rows, want 2 (rejected credential, policy denial)\n%s", len(decisions), smokescreen.CanonicalProxyDecision, p.logs)
	}
	for _, row := range append(required, decisions...) {
		if _, leaked := row[logFieldPreAuth]; leaked {
			t.Errorf("internal marker %q leaked into the audit row", logFieldPreAuth)
		}
	}
	var sawIdentityDenial bool
	for _, row := range decisions {
		if row["allow"] != false {
			t.Errorf("decision row is not a denial: %v", row)
		}
		if reason, _ := row["decision_reason"].(string); strings.Contains(reason, "empty username") {
			sawIdentityDenial = true
		}
	}
	if !sawIdentityDenial {
		t.Errorf("rejected credential's row does not carry the role func's own error\n%s", p.logs)
	}
}

// Anti-SSRF stays on: a destination in a private range is refused even for a workload whose
// action is `open` (which would allow any public host), both with the deployed explicit
// deny_ranges and with none configured at all, where Smokescreen's own default must still
// hold. Literal RFC 1918 addresses are used because the range check runs on the resolved
// address before any dial: a refusal is immediate, whereas an allowed one would sit in the
// (10 s) connect timeout and trip the client's read deadline in connect().
func TestPrivateRangesDeniedEvenWhenOpen(t *testing.T) {
	deployed := "deny_ranges:\n  - 10.0.0.0/8\n  - 172.16.0.0/12\n  - 192.168.0.0/16\n  - 169.254.0.0/16\n  - 127.0.0.0/8\n"
	for name, extra := range map[string]string{"deployed deny_ranges": deployed, "defaults only": ""} {
		t.Run(name, func(t *testing.T) {
			p := startProxy(t, "basic-name", renderSmokescreenACL("basic-name", []module{
				{ID: "svc", AllowedHosts: []string{"unused.example"}, Action: "open"},
			}, nil), extra)

			for _, target := range []string{"10.255.255.1:443", "192.168.1.1:443", "169.254.169.254:80"} {
				resp := p.connect(t, target, basicHeader("svc", "x"))
				if resp.StatusCode == http.StatusOK {
					t.Fatalf("CONNECT to %s was established", target)
				}
			}
			rows := p.rows(t, smokescreen.CanonicalProxyDecision)
			if len(rows) != 3 {
				t.Fatalf("want 3 decision rows, got %d\n%s", len(rows), p.logs)
			}
			for _, row := range rows {
				if row["allow"] != false {
					t.Errorf("private destination not denied: %v", row)
				}
			}
		})
	}
}
