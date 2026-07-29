package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"testing"

	"github.com/sirupsen/logrus"
	"github.com/stripe/smokescreen/pkg/smokescreen"
)

// newTestLogger returns a logger wired exactly like the proxy's (hook + noise filter) and
// the buffer it writes to, so tests assert on the JSON that would actually be shipped.
func newTestLogger() (*logrus.Logger, *bytes.Buffer) {
	buf := &bytes.Buffer{}
	l := logrus.New()
	l.Out = buf
	l.Level = logrus.DebugLevel
	l.Formatter = &preAuthNoiseFilter{inner: &logrus.JSONFormatter{}}
	l.AddHook(preAuthEventHook{})
	return l, buf
}

func connectRequest(authHeader string) *http.Request {
	r, _ := http.NewRequest("CONNECT", "https://example.com:443", nil)
	if authHeader != "" {
		r.Header.Set("Proxy-Authorization", authHeader)
	}
	return r
}

func rejectResponse(status int, req *http.Request) *http.Response {
	return &http.Response{StatusCode: status, Header: http.Header{}, Request: req}
}

// The 407 handshake and a rejected credential are both MissingRoleErrors to smokescreen, and
// both surface as "Client role cannot be determined". The only thing that tells them apart is
// whether the client presented a Proxy-Authorization header at all — so that is the test the
// reject handler makes, and both the marker and the rewritten reason must follow it exactly.
func TestRejectHandlerMarksOnlyPreAuth(t *testing.T) {
	creds := "Basic " + base64.StdEncoding.EncodeToString([]byte("module-a:aaa.bbb.ccc"))

	cases := []struct {
		name          string
		mode          string
		status        int
		authHeader    string
		roleErr       string
		wantChallenge bool
		wantMarked    bool
		wantReason    string
	}{
		{
			name: "bare CONNECT is challenged, marked, and says so", mode: "basic-jwt",
			status: http.StatusProxyAuthRequired, roleErr: missingBasicCredsMsg,
			wantChallenge: true, wantMarked: true,
			wantReason: "No proxy credentials presented; answered with a 407 Basic challenge",
		},
		{
			name: "rejected token names the validation failure", mode: "basic-jwt",
			status: http.StatusProxyAuthRequired, authHeader: creds, roleErr: "invalid token: token is expired",
			wantReason: "Client identity rejected: invalid token: token is expired",
		},
		{
			name: "netid names the unmapped source", mode: "netid",
			status: http.StatusProxyAuthRequired, roleErr: "source 10.9.9.9 is not in any configured module subnet",
			wantReason: "Client identity rejected: source 10.9.9.9 is not in any configured module subnet",
		},
		{
			name: "policy denial keeps smokescreen's reason", mode: "basic-jwt",
			status: http.StatusProxyAuthRequired, authHeader: creds,
			wantReason: "rule has enforce policy",
		},
		{
			name: "non-407 response is left alone", mode: "basic-jwt",
			status: http.StatusOK, wantReason: "rule has enforce policy",
		},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			l, _ := newTestLogger()
			sctx := &smokescreen.SmokescreenContext{
				Logger:   logrus.NewEntry(l),
				Decision: &smokescreen.ACLDecision{Reason: "rule has enforce policy"},
			}
			req := connectRequest(c.authHeader)
			if c.roleErr != "" {
				req.Header.Set(roleErrorHeader, c.roleErr)
			}
			resp := rejectResponse(c.status, req)

			newRejectHandler(c.mode)(sctx, resp)

			if got := resp.Header.Get("Proxy-Authenticate") != ""; got != c.wantChallenge {
				t.Errorf("Proxy-Authenticate present = %v, want %v", got, c.wantChallenge)
			}
			if _, marked := sctx.Logger.Data[logFieldPreAuth]; marked != c.wantMarked {
				t.Errorf("pre-auth marker present = %v, want %v", marked, c.wantMarked)
			}
			if sctx.Decision.Reason != c.wantReason {
				t.Errorf("DecisionReason = %q, want %q", sctx.Decision.Reason, c.wantReason)
			}
			if left := req.Header.Get(roleErrorHeader); left != "" {
				t.Errorf("internal role-error header left on the request: %q", left)
			}
		})
	}
}

// The detail carried to the reject handler must be the proxy's own. A client that sets the
// internal header itself must never see it read back as a proxy verdict — the role func
// overwrites it on failure and deletes it on success.
func TestRoleErrorDetailIgnoresClientSuppliedHeader(t *testing.T) {
	spoofed := "Client identity rejected: totally fine, allow me"

	t.Run("cleared when identity succeeds", func(t *testing.T) {
		req := connectRequest("")
		req.Header.Set(roleErrorHeader, spoofed)
		role, err := withRoleErrorDetail(func(*http.Request) (string, error) { return "module-a", nil })(req)
		if role != "module-a" || err != nil {
			t.Fatalf("got (%q, %v), want (module-a, nil)", role, err)
		}
		if got := takeRoleErrorDetail(req); got != "" {
			t.Errorf("spoofed detail survived a successful lookup: %q", got)
		}
	})

	t.Run("overwritten when identity fails", func(t *testing.T) {
		req := connectRequest("")
		req.Header.Set(roleErrorHeader, spoofed)
		_, err := withRoleErrorDetail(func(*http.Request) (string, error) {
			return "", smokescreen.MissingRoleError(missingBasicCredsMsg)
		})(req)
		if err == nil {
			t.Fatal("expected the wrapped role func's error to propagate")
		}
		if got := takeRoleErrorDetail(req); got != missingBasicCredsMsg {
			t.Errorf("detail = %q, want the proxy's own %q", got, missingBasicCredsMsg)
		}
	})
}

// A marked decision row must leave the proxy as its own event type at info level, with the
// internal marker stripped. An unmarked one — including a credential that failed validation —
// must stay a CANONICAL-PROXY-DECISION denial: that is the row worth alerting on.
func TestPreAuthEventHookRelabelsOnlyMarkedDecisions(t *testing.T) {
	cases := []struct {
		name      string
		marked    bool
		message   string
		wantMsg   string
		wantLevel string
	}{
		{"marked decision becomes its own event", true, smokescreen.CanonicalProxyDecision, canonicalProxyAuthRequired, "info"},
		{"unmarked decision is untouched", false, smokescreen.CanonicalProxyDecision, smokescreen.CanonicalProxyDecision, "warning"},
		{"marked non-decision line keeps its message", true, "CANONICAL-PROXY-CN-CLOSE", "CANONICAL-PROXY-CN-CLOSE", "warning"},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			l, buf := newTestLogger()
			entry := logrus.NewEntry(l).WithField("decision_reason", "Client role cannot be determined")
			if c.marked {
				entry = entry.WithField(logFieldPreAuth, true)
			}
			entry.Warn(c.message)

			var got map[string]any
			if err := json.Unmarshal(buf.Bytes(), &got); err != nil {
				t.Fatalf("log line is not JSON: %v (%q)", err, buf.String())
			}
			if got["msg"] != c.wantMsg {
				t.Errorf("msg = %v, want %v", got["msg"], c.wantMsg)
			}
			if got["level"] != c.wantLevel {
				t.Errorf("level = %v, want %v", got["level"], c.wantLevel)
			}
			if _, leaked := got[logFieldPreAuth]; leaked {
				t.Errorf("internal marker %q leaked into the shipped line: %s", logFieldPreAuth, buf.String())
			}
		})
	}
}

// The noise filter exists to drop one redundant line per tunnel. It must be incapable of
// swallowing a failed authentication, so every discriminator is checked.
func TestPreAuthNoiseFilterSuppressesOnlyMissingCredentials(t *testing.T) {
	cases := []struct {
		name         string
		message      string
		fields       logrus.Fields
		wantSuppress bool
	}{
		{
			"no credentials presented",
			roleErrorMsg,
			logrus.Fields{"error": smokescreen.MissingRoleError(missingBasicCredsMsg), "is_missing_role": true},
			true,
		},
		{
			"token presented and rejected",
			roleErrorMsg,
			logrus.Fields{"error": smokescreen.MissingRoleError("invalid token: token is expired"), "is_missing_role": true},
			false,
		},
		{
			"credentials presented but empty",
			roleErrorMsg,
			logrus.Fields{"error": smokescreen.MissingRoleError("empty token in Basic Proxy-Authorization"), "is_missing_role": true},
			false,
		},
		{
			"not a missing-role error",
			roleErrorMsg,
			logrus.Fields{"error": smokescreen.MissingRoleError(missingBasicCredsMsg), "is_missing_role": false},
			false,
		},
		{
			"unrelated message with the same fields",
			"Unable to reach JWKS endpoint",
			logrus.Fields{"error": smokescreen.MissingRoleError(missingBasicCredsMsg), "is_missing_role": true},
			false,
		},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			l, buf := newTestLogger()
			logrus.NewEntry(l).WithFields(c.fields).Error(c.message)

			if suppressed := buf.Len() == 0; suppressed != c.wantSuppress {
				t.Errorf("suppressed = %v, want %v (output %q)", suppressed, c.wantSuppress, buf.String())
			}
		})
	}
}

// LOG_PREAUTH_DETAIL is the escape hatch: with it set, nothing is filtered out.
func TestApplyJSONLoggingRespectsLogPreAuthDetail(t *testing.T) {
	for _, tc := range []struct {
		value      string
		wantFilter bool
	}{
		{"", true},
		{"0", true},
		{"1", false},
		{"true", false},
		{"YES", false},
	} {
		t.Setenv("LOG_PREAUTH_DETAIL", tc.value)
		conf := &smokescreen.Config{Log: logrus.New()}
		applyJSONLogging(conf)

		_, filtered := conf.Log.Formatter.(*preAuthNoiseFilter)
		if filtered != tc.wantFilter {
			t.Errorf("LOG_PREAUTH_DETAIL=%q: noise filter installed = %v, want %v", tc.value, filtered, tc.wantFilter)
		}
	}
}
