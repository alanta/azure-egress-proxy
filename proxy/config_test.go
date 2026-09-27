package main

import (
	"bytes"
	"reflect"
	"strings"
	"testing"

	"github.com/sirupsen/logrus"
)

// envMap is a getenv over a fixed environment.
func envMap(m map[string]string) func(string) string {
	return func(k string) string { return m[k] }
}

// goodJWTEnv is a complete basic-jwt configuration; cases override single keys.
func goodJWTEnv(overrides map[string]string) map[string]string {
	env := map[string]string{
		"SMOKESCREEN_ID_MODE": "basic-jwt",
		"JWKS_URL":            "https://login.microsoftonline.com/tenant/discovery/v2.0/keys",
		"EXPECT_ISS":          "https://login.microsoftonline.com/tenant/v2.0",
		"EXPECT_AUD":          "00000000-0000-0000-0000-000000000001",
	}
	for k, v := range overrides {
		env[k] = v
	}
	return env
}

func problemKeys(probs []configProblem) []string {
	keys := []string{}
	for _, p := range probs {
		keys = append(keys, p.key)
	}
	return keys
}

// Every gap from issue #85 is caught by name, in both managed and standalone mode: an unset
// or unknown mode (which used to mean netid), an empty issuer (which switched the issuer and
// tenant check off), an empty audience, and a JWKS URL that is not well-formed https.
func TestIdentityConfigProblems(t *testing.T) {
	cases := []struct {
		name     string
		env      map[string]string
		wantKeys []string
		wantText string // substring of the first problem's reason
	}{
		{"complete basic-jwt", goodJWTEnv(nil), []string{}, ""},
		{"complete jwt", goodJWTEnv(map[string]string{"SMOKESCREEN_ID_MODE": "jwt"}), []string{}, ""},
		{"basic-name needs nothing else", map[string]string{"SMOKESCREEN_ID_MODE": "basic-name"}, []string{}, ""},

		{"unset mode", map[string]string{}, []string{"SMOKESCREEN_ID_MODE"}, "not set"},
		{"unset mode with jwt keys present", goodJWTEnv(map[string]string{"SMOKESCREEN_ID_MODE": ""}),
			[]string{"SMOKESCREEN_ID_MODE"}, "not set"},
		{"unknown mode", goodJWTEnv(map[string]string{"SMOKESCREEN_ID_MODE": "basic_jwt"}),
			[]string{"SMOKESCREEN_ID_MODE"}, `unknown value "basic_jwt"`},
		{"mode is case-sensitive", goodJWTEnv(map[string]string{"SMOKESCREEN_ID_MODE": "Basic-JWT"}),
			[]string{"SMOKESCREEN_ID_MODE"}, "unknown value"},

		{"empty issuer", goodJWTEnv(map[string]string{"EXPECT_ISS": ""}), []string{"EXPECT_ISS"}, "issuer"},
		{"blank issuer", goodJWTEnv(map[string]string{"EXPECT_ISS": "  "}), []string{"EXPECT_ISS"}, "issuer"},
		{"empty audience", goodJWTEnv(map[string]string{"EXPECT_AUD": ""}), []string{"EXPECT_AUD"}, "audience"},

		{"empty JWKS URL", goodJWTEnv(map[string]string{"JWKS_URL": ""}), []string{"JWKS_URL"}, "not set"},
		{"http JWKS URL", goodJWTEnv(map[string]string{"JWKS_URL": "http://login.microsoftonline.com/keys"}),
			[]string{"JWKS_URL"}, "must be https"},
		{"other scheme", goodJWTEnv(map[string]string{"JWKS_URL": "ftp://idp.example/keys"}),
			[]string{"JWKS_URL"}, "must be https"},
		{"no scheme", goodJWTEnv(map[string]string{"JWKS_URL": "login.microsoftonline.com/tenant/keys"}),
			[]string{"JWKS_URL"}, "not an absolute https URL"},
		{"no host", goodJWTEnv(map[string]string{"JWKS_URL": "https:///keys"}),
			[]string{"JWKS_URL"}, "not an absolute https URL"},
		{"unparseable", goodJWTEnv(map[string]string{"JWKS_URL": "https://idp.example/%zz"}),
			[]string{"JWKS_URL"}, "not a valid URL"},

		{"several missing keys reported together", map[string]string{"SMOKESCREEN_ID_MODE": "jwt", "JWKS_URL": "http://idp/keys"},
			[]string{"JWKS_URL", "EXPECT_ISS", "EXPECT_AUD"}, "must be https"},
	}
	for _, c := range cases {
		for _, managed := range []bool{true, false} {
			probs := identityConfigProblems(envMap(c.env), managed)
			if got := problemKeys(probs); !reflect.DeepEqual(got, c.wantKeys) {
				t.Errorf("%s (managed=%v): keys = %v, want %v (%v)", c.name, managed, got, c.wantKeys, probs)
				continue
			}
			if c.wantText != "" && !strings.Contains(probs[0].reason, c.wantText) {
				t.Errorf("%s (managed=%v): reason %q does not mention %q", c.name, managed, probs[0].reason, c.wantText)
			}
		}
	}
}

// netid stays available, but only when named. Standalone it needs SUBNET_ROLES; managed it
// takes its subnets from the allowlist modules, so nothing else is required.
func TestIdentityConfigNetID(t *testing.T) {
	named := map[string]string{"SMOKESCREEN_ID_MODE": "netid"}
	if probs := identityConfigProblems(envMap(named), true); len(probs) != 0 {
		t.Errorf("managed netid: %v, want no problems", probs)
	}
	if got := problemKeys(identityConfigProblems(envMap(named), false)); !reflect.DeepEqual(got, []string{"SUBNET_ROLES"}) {
		t.Errorf("standalone netid without SUBNET_ROLES: keys = %v, want [SUBNET_ROLES]", got)
	}
	named["SUBNET_ROLES"] = "10.1.0.0/23=sample-app"
	if probs := identityConfigProblems(envMap(named), false); len(probs) != 0 {
		t.Errorf("standalone netid with SUBNET_ROLES: %v, want no problems", probs)
	}
	// SUBNET_ROLES alone does not select netid.
	if got := problemKeys(identityConfigProblems(envMap(map[string]string{"SUBNET_ROLES": "10.1.0.0/23=a"}), false)); !reflect.DeepEqual(got, []string{"SMOKESCREEN_ID_MODE"}) {
		t.Errorf("SUBNET_ROLES without a mode: keys = %v, want [SMOKESCREEN_ID_MODE]", got)
	}
}

// Plain-http JWKS is refused unless the local-development escape hatch is set, and the hatch
// relaxes only the scheme: a malformed URL, or a missing issuer, still fails.
func TestIdentityConfigInsecureJWKSEscapeHatch(t *testing.T) {
	httpURL := map[string]string{"JWKS_URL": "http://mock-idp:8080/jwks"}
	for _, v := range []string{"", "0", "false"} {
		httpURL[allowInsecureJWKSEnv] = v
		if got := problemKeys(identityConfigProblems(envMap(goodJWTEnv(httpURL)), true)); !reflect.DeepEqual(got, []string{"JWKS_URL"}) {
			t.Errorf("%s=%q: keys = %v, want [JWKS_URL]", allowInsecureJWKSEnv, v, got)
		}
	}
	for _, v := range []string{"1", "true"} {
		httpURL[allowInsecureJWKSEnv] = v
		if probs := identityConfigProblems(envMap(goodJWTEnv(httpURL)), true); len(probs) != 0 {
			t.Errorf("%s=%q: %v, want no problems", allowInsecureJWKSEnv, v, probs)
		}
	}
	stillChecked := goodJWTEnv(map[string]string{
		allowInsecureJWKSEnv: "1", "JWKS_URL": "mock-idp:8080/jwks", "EXPECT_ISS": "",
	})
	if got := problemKeys(identityConfigProblems(envMap(stillChecked), true)); !reflect.DeepEqual(got, []string{"JWKS_URL", "EXPECT_ISS"}) {
		t.Errorf("escape hatch must not relax anything but the scheme: keys = %v", got)
	}
}

// A failed check is one log line naming every failing key with its reason, then a non-zero
// exit. A passing one exits nothing, and warns when the http escape hatch is in use.
func TestRequireIdentityConfig(t *testing.T) {
	run := func(env map[string]string) (out string, exitCode int, exited bool) {
		var buf bytes.Buffer
		l := logrus.New()
		l.SetOutput(&buf)
		l.ExitFunc = func(code int) { exitCode, exited = code, true }
		requireIdentityConfig(l, envMap(env), true)
		return buf.String(), exitCode, exited
	}

	out, code, exited := run(map[string]string{"SMOKESCREEN_ID_MODE": "basic-jwt", "JWKS_URL": "http://idp/keys"})
	if !exited || code == 0 {
		t.Fatalf("exited=%v code=%d, want a non-zero exit", exited, code)
	}
	if lines := strings.Count(strings.TrimSpace(out), "\n") + 1; lines != 1 {
		t.Errorf("got %d log lines, want 1:\n%s", lines, out)
	}
	for _, want := range []string{"level=fatal", "JWKS_URL: ", "must be https", "EXPECT_ISS: not set", "EXPECT_AUD: not set"} {
		if !strings.Contains(out, want) {
			t.Errorf("log line does not contain %q:\n%s", want, out)
		}
	}

	out, _, exited = run(goodJWTEnv(nil))
	if exited || out != "" {
		t.Errorf("valid config: exited=%v, output %q; want neither", exited, out)
	}

	out, _, exited = run(goodJWTEnv(map[string]string{"JWKS_URL": "http://mock-idp:8080/jwks", allowInsecureJWKSEnv: "1"}))
	if exited || !strings.Contains(out, "level=warning") || !strings.Contains(out, allowInsecureJWKSEnv) {
		t.Errorf("escape hatch: exited=%v, output %q; want a warning naming %s", exited, out, allowInsecureJWKSEnv)
	}
}

// The role-func constructors no longer default to netid: an unset or unknown mode is fatal
// in both entry points even if the startup check were bypassed.
func TestRoleFuncsHaveNoDefaultMode(t *testing.T) {
	l := logrus.StandardLogger()
	oldOut, oldExit := l.Out, l.ExitFunc
	defer func() { l.SetOutput(oldOut); l.ExitFunc = oldExit }()
	l.SetOutput(&bytes.Buffer{})
	type exit struct{}
	l.ExitFunc = func(int) { panic(exit{}) }

	fatal := func(f func()) (fataled bool) {
		defer func() {
			if r := recover(); r != nil {
				if _, ok := r.(exit); !ok {
					panic(r)
				}
				fataled = true
			}
		}()
		f()
		return false
	}
	for _, mode := range []string{"", "bogus"} {
		if !fatal(func() { newManagedRoleFunc(mode) }) {
			t.Errorf("newManagedRoleFunc(%q) did not exit", mode)
		}
		if !fatal(func() { roleFromRequest(mode) }) {
			t.Errorf("roleFromRequest(%q) did not exit", mode)
		}
	}
}
