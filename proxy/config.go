// Startup check of the identity configuration.
//
// The proxy must not serve with an identity configuration that is missing or wrong. Several
// of those gaps fail OPEN rather than closed: an empty EXPECT_ISS switches golang-jwt's issuer
// check off (and with it the tenant check, since Entra signing keys are shared across
// tenants), and a missing or misspelled SMOKESCREEN_ID_MODE used to fall through to
// source-subnet identity. Others fail closed but unhelpfully: an empty EXPECT_AUD rejects
// every token as "invalid audience".
//
// So both entry points (standalone and managed) run this check before anything listens, and
// on any problem the process logs every failing key in one line and exits. It deliberately
// does not fall back to a deny-all listener: an open port passes the TCP health probe, so a
// misconfigured instance would look healthy to the load balancer and to automatic OS upgrades.
package main

import (
	"fmt"
	"net/url"
	"strings"

	"github.com/sirupsen/logrus"
)

// identityModes are the SMOKESCREEN_ID_MODE values the proxy knows. None of them is implied:
// the mode must be named, netid included.
var identityModes = []string{"basic-jwt", "jwt", "basic-name", "netid"}

// allowInsecureJWKSEnv is the local-development escape hatch that lets JWKS_URL use plain
// http (the Aspire stack's mock IdP serves its JWKS over http). Off unless set; it relaxes the
// scheme only — the URL must still be well-formed, and the issuer and audience are still
// required. Never set it in a deployment: keys fetched over http can be replaced in transit.
const allowInsecureJWKSEnv = "JWKS_ALLOW_INSECURE_HTTP"

// configProblem is one failing configuration key and why it fails.
type configProblem struct {
	key    string
	reason string
}

func (p configProblem) String() string { return p.key + ": " + p.reason }

// identityConfigProblems returns every problem with the identity configuration, not just the
// first, so one failed start tells the operator everything to fix. managed is true in managed
// mode, where netid derives its subnet map from the allowlist modules instead of SUBNET_ROLES.
func identityConfigProblems(getenv func(string) string, managed bool) []configProblem {
	var probs []configProblem
	mode := getenv("SMOKESCREEN_ID_MODE")
	switch mode {
	case "":
		probs = append(probs, configProblem{"SMOKESCREEN_ID_MODE",
			"not set; want one of " + strings.Join(identityModes, ", ")})
	case "jwt", "basic-jwt":
		probs = append(probs, jwtConfigProblems(getenv)...)
	case "netid":
		if !managed {
			if _, err := parseSubnetRoles(getenv("SUBNET_ROLES")); err != nil {
				probs = append(probs, configProblem{"SUBNET_ROLES", err.Error()})
			}
		}
	case "basic-name":
		// Identity is the Basic username; nothing to configure.
	default:
		probs = append(probs, configProblem{"SMOKESCREEN_ID_MODE",
			fmt.Sprintf("unknown value %q; want one of %s", mode, strings.Join(identityModes, ", "))})
	}
	return probs
}

// jwtConfigProblems checks what token validation needs in the jwt and basic-jwt modes.
func jwtConfigProblems(getenv func(string) string) []configProblem {
	var probs []configProblem
	if reason := jwksURLProblem(getenv("JWKS_URL"), envEnabledIn(getenv, allowInsecureJWKSEnv)); reason != "" {
		probs = append(probs, configProblem{"JWKS_URL", reason})
	}
	if strings.TrimSpace(getenv("EXPECT_ISS")) == "" {
		probs = append(probs, configProblem{"EXPECT_ISS",
			"not set; without it the token issuer, and with it the tenant, is not checked"})
	}
	if strings.TrimSpace(getenv("EXPECT_AUD")) == "" {
		probs = append(probs, configProblem{"EXPECT_AUD",
			"not set; every token would be rejected as having the wrong audience"})
	}
	return probs
}

// jwksURLProblem returns why raw is not a usable JWKS URL, or "" if it is. It must be an
// absolute https URL with a host; plain http is accepted only with the escape hatch.
func jwksURLProblem(raw string, allowHTTP bool) string {
	if strings.TrimSpace(raw) == "" {
		return "not set"
	}
	u, err := url.Parse(raw)
	if err != nil {
		return fmt.Sprintf("not a valid URL: %v", err)
	}
	// Hostname, not Host: "https://:443/keys" has Host ":443" but no host to fetch from.
	// Checked before the scheme, so the http escape hatch gets the same check.
	if u.Scheme == "" || u.Hostname() == "" {
		return fmt.Sprintf("%q is not an absolute https URL", raw)
	}
	switch {
	case u.Scheme == "https":
		return ""
	case u.Scheme == "http" && allowHTTP:
		return ""
	case u.Scheme == "http":
		return fmt.Sprintf("%q uses http; it must be https, because signing keys fetched over "+
			"plain http can be replaced in transit (%s=1 allows http for local development only)",
			raw, allowInsecureJWKSEnv)
	default:
		return fmt.Sprintf("%q has scheme %q; it must be https", raw, u.Scheme)
	}
}

// requireIdentityConfig runs the startup check and, on any problem, logs every failing key in
// a single line and exits non-zero through l.Fatal. It must run before anything listens.
func requireIdentityConfig(l *logrus.Logger, getenv func(string) string, managed bool) {
	probs := identityConfigProblems(getenv, managed)
	if len(probs) > 0 {
		keys := make([]string, len(probs))
		msgs := make([]string, len(probs))
		for i, p := range probs {
			keys[i] = p.key
			msgs[i] = p.String()
		}
		l.WithField("invalid_config", keys).Fatalf(
			"invalid identity configuration, refusing to start: %s", strings.Join(msgs, "; "))
		return
	}
	mode := getenv("SMOKESCREEN_ID_MODE")
	if (mode == "jwt" || mode == "basic-jwt") && strings.HasPrefix(strings.ToLower(getenv("JWKS_URL")), "http:") {
		l.Warnf("%s is set: fetching signing keys over plain http from %s. For local development only.",
			allowInsecureJWKSEnv, getenv("JWKS_URL"))
	}
}

// envEnabledIn is envEnabled against an arbitrary environment lookup.
func envEnabledIn(getenv func(string) string, name string) bool {
	switch strings.ToLower(strings.TrimSpace(getenv(name))) {
	case "1", "true", "yes", "on":
		return true
	}
	return false
}
