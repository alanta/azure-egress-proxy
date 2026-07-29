// egress-proxy: a custom Smokescreen entrypoint with Azure-native workload identity.
//
// Stripe's stock smokescreen binary derives the client role from the TLS client-cert CN.
// That doesn't fit Azure Container Apps and friends, where services hold no client certs.
// Smokescreen is designed to be embedded as a library so you can supply your own
// RoleFromRequest — which is what this binary does.
//
// The identity mechanism is chosen at runtime via SMOKESCREEN_ID_MODE:
//
//	netid : identity = SOURCE SUBNET. The client cannot influence its source subnet, so
//	        this is unspoofable and needs no client cooperation. Granularity is the
//	        subnet; only sound where a workload is genuinely pinned to one.
//	jwt   : identity = a validated managed-identity bearer token carried in
//	        `Proxy-Authorization: Bearer ...` on the CONNECT — for clients that can set
//	        CONNECT headers (Go, curl). .NET's HttpClient cannot without custom socket code.
//
// The Basic modes carry identity in HTTP Basic *proxy* auth, which .NET (and most
// runtimes) DO emit natively after a 407 Proxy-Authenticate: Basic challenge (see
// basicChallengeHandler), via DefaultProxyCredentials:
//
//	basic-name : identity = the Basic USERNAME (simple, spoofable service-name model).
//	basic-jwt  : identity = a managed-identity JWT carried in the Basic PASSWORD;
//	             validated exactly like `jwt` mode (sig/iss/aud/exp), role = appid.
//	             The recommended mode — see docs/identity.md.
//
// The returned role string must match a services[].name in the rendered ACL.
package main

import (
	"encoding/base64"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/MicahParks/keyfunc/v3"
	"github.com/golang-jwt/jwt/v5"
	"github.com/sirupsen/logrus"
	"github.com/stripe/smokescreen/cmd"
	"github.com/stripe/smokescreen/pkg/smokescreen"
)

// canonicalProxyAuthRequired is this proxy's own event type for the credential-less first
// CONNECT of a tunnel — the client half of the 407 Basic handshake, not a policy denial.
// Smokescreen logs it as a CANONICAL-PROXY-DECISION with reason "Client role cannot be
// determined"; we relabel it (see preAuthEventHook) so the decision stream carries verdicts
// only. It keeps the CANONICAL-PROXY prefix, which is what the DCR splits the syslog stream
// on, so the rows still land in EgressProxy_CL with no ingestion change.
const canonicalProxyAuthRequired = "CANONICAL-PROXY-AUTH-REQUIRED"

// logFieldPreAuth marks a log entry as belonging to the pre-auth challenge. Set on the
// request's logger by basicChallengeHandler (which runs before the deferred decision log)
// and consumed — and removed — by preAuthEventHook.
const logFieldPreAuth = "egress_pre_auth"

// missingBasicCredsMsg is the MissingRoleError text for "no Proxy-Authorization header at
// all", as opposed to credentials that were presented and found wanting (empty, malformed,
// or an invalid token — those get their own text). It is the only role error the pre-auth
// noise filter is allowed to suppress.
const missingBasicCredsMsg = "no Basic Proxy-Authorization"

// roleErrorMsg is smokescreen's log message for a failed RoleFromRequest (config.go,
// getRole). One is emitted per credential-less CONNECT, alongside the decision row.
const roleErrorMsg = "Unable to get role for request"

// roleErrorHeader is an internal request header used to carry an identity failure from the
// role func to the reject handler. Never trusted from the client and never forwarded: the
// role func overwrites or deletes it on every request, and the handler clears it on read.
const roleErrorHeader = "X-Egress-Role-Error"

type cidrRole struct {
	net  *net.IPNet
	role string
}

// parseSubnetRoles reads the subnet->role mapping from configuration (the SUBNET_ROLES
// env var), format: "cidr=role,cidr=role". This is DATA, not code: in a real deployment
// it is rendered by deployment tooling alongside the ACL — changing network topology
// must never require a recompile.
func parseSubnetRoles(spec string) ([]cidrRole, error) {
	var out []cidrRole
	for _, pair := range strings.Split(spec, ",") {
		pair = strings.TrimSpace(pair)
		if pair == "" {
			continue
		}
		kv := strings.SplitN(pair, "=", 2)
		if len(kv) != 2 {
			return nil, fmt.Errorf("bad entry %q (want cidr=role)", pair)
		}
		_, n, err := net.ParseCIDR(strings.TrimSpace(kv[0]))
		if err != nil {
			return nil, fmt.Errorf("bad CIDR in %q: %w", pair, err)
		}
		out = append(out, cidrRole{n, strings.TrimSpace(kv[1])})
	}
	if len(out) == 0 {
		return nil, fmt.Errorf("no entries (set SUBNET_ROLES=\"cidr=role,...\")")
	}
	return out, nil
}

// Role from source subnet, using the configured mapping.
func newNetIDRole() func(*http.Request) (string, error) {
	roles, err := parseSubnetRoles(os.Getenv("SUBNET_ROLES"))
	if err != nil {
		logrus.Fatalf("SUBNET_ROLES: %v", err)
	}
	return netIDRoleFunc(roles)
}

// netIDRoleFunc returns the source-subnet RoleFromRequest for a given mapping. The mapping
// comes from the SUBNET_ROLES env (standalone) or from the allowlist-blob modules (managed mode).
func netIDRoleFunc(roles []cidrRole) func(*http.Request) (string, error) {
	return func(req *http.Request) (string, error) {
		host, _, err := net.SplitHostPort(req.RemoteAddr)
		if err != nil {
			host = req.RemoteAddr
		}
		ip := net.ParseIP(host)
		for _, cr := range roles {
			if ip != nil && cr.net.Contains(ip) {
				return cr.role, nil
			}
		}
		return "", smokescreen.MissingRoleError(
			fmt.Sprintf("source %s is not in any configured module subnet", host))
	}
}

// newJWTValidator loads the IdP's JWKS once and returns a function that fully validates a
// managed-identity JWT (RS256 signature against the JWKS, issuer, audience, expiry, with a
// small clock-skew leeway) and returns the app's identity claim (`appid` on v1/MI tokens,
// `azp` on v2). Shared by `jwt` mode (token in the Bearer header) and `basic-jwt` mode
// (token in the Basic password), so the trust check is identical regardless of how the
// token is carried.
func newJWTValidator() func(token string) (string, error) {
	jwksURL := os.Getenv("JWKS_URL")
	iss := os.Getenv("EXPECT_ISS")
	aud := os.Getenv("EXPECT_AUD")

	// JWKS may not be up at boot; retry briefly.
	var k keyfunc.Keyfunc
	var err error
	for i := 0; i < 30; i++ {
		k, err = keyfunc.NewDefault([]string{jwksURL})
		if err == nil {
			break
		}
		time.Sleep(time.Second)
	}
	if err != nil {
		logrus.Fatalf("could not load JWKS from %s: %v", jwksURL, err)
	}

	return func(token string) (string, error) {
		tok, err := jwt.Parse(token, k.Keyfunc,
			jwt.WithValidMethods([]string{"RS256"}),
			jwt.WithIssuer(iss),
			jwt.WithAudience(aud),
			jwt.WithExpirationRequired(),
			// Tolerate small clock skew between the IdP and the proxy host so a
			// just-issued token isn't spuriously rejected on nbf/exp.
			jwt.WithLeeway(60*time.Second),
		)
		if err != nil || !tok.Valid {
			return "", smokescreen.MissingRoleError(fmt.Sprintf("invalid token: %v", err))
		}
		claims, _ := tok.Claims.(jwt.MapClaims)
		return appIDFromClaims(claims)
	}
}

// appIDFromClaims returns the app's own identity claim, the role. It is `appid` on Entra
// v1 / managed-identity tokens (iss = sts.windows.net/<tenant>/) and `azp` on v2 tokens
// (iss = login.microsoftonline.com/<tenant>/v2.0); accept either so the proxy works
// regardless of token version. For a managed identity both carry the identity's client ID.
func appIDFromClaims(claims jwt.MapClaims) (string, error) {
	if v, _ := claims["appid"].(string); v != "" {
		return v, nil
	}
	if v, _ := claims["azp"].(string); v != "" {
		return v, nil
	}
	return "", smokescreen.MissingRoleError("token has no appid/azp claim")
}

// Role from a validated managed-identity bearer token in Proxy-Authorization.
func newJWTRole() func(*http.Request) (string, error) {
	validate := newJWTValidator()
	return func(req *http.Request) (string, error) {
		h := req.Header.Get("Proxy-Authorization")
		parts := strings.SplitN(h, " ", 2)
		if len(parts) != 2 || !strings.EqualFold(parts[0], "bearer") {
			return "", smokescreen.MissingRoleError("no bearer token in Proxy-Authorization")
		}
		return validate(parts[1])
	}
}

// basicProxyCreds extracts the username/password from a `Proxy-Authorization: Basic ...`
// header (the standard "user:pass" base64). ok=false when the header is absent or not Basic,
// which the role funcs map to MissingRoleError -> 407 challenge.
func basicProxyCreds(req *http.Request) (user, pass string, ok bool) {
	h := req.Header.Get("Proxy-Authorization")
	parts := strings.SplitN(h, " ", 2)
	if len(parts) != 2 || !strings.EqualFold(parts[0], "basic") {
		return "", "", false
	}
	dec, err := base64.StdEncoding.DecodeString(strings.TrimSpace(parts[1]))
	if err != nil {
		return "", "", false
	}
	up := strings.SplitN(string(dec), ":", 2)
	if len(up) != 2 {
		return "", "", false
	}
	return up[0], up[1], true
}

// Role = the Basic USERNAME. Simple and spoofable (any client can claim any service
// name), but ~1 line of client config (DefaultProxyCredentials). For bootstrap and
// low-trust setups only.
func newBasicNameRole() func(*http.Request) (string, error) {
	return func(req *http.Request) (string, error) {
		user, _, ok := basicProxyCreds(req)
		if !ok {
			return "", smokescreen.MissingRoleError(missingBasicCredsMsg)
		}
		if user == "" {
			// Credentials WERE presented, just unusable — a different failure from the
			// handshake, and one whose diagnostic line must survive the noise filter.
			return "", smokescreen.MissingRoleError("empty username in Basic Proxy-Authorization")
		}
		return user, nil
	}
}

// Role = appid from a managed-identity JWT carried in the Basic PASSWORD. Same trust as
// `jwt` mode, but the token rides in Basic auth that clients emit natively, so no custom
// tunnelling code. Username is informational only (we validate the password token).
func newBasicJWTRole() func(*http.Request) (string, error) {
	validate := newJWTValidator()
	return func(req *http.Request) (string, error) {
		_, pass, ok := basicProxyCreds(req)
		if !ok {
			return "", smokescreen.MissingRoleError(missingBasicCredsMsg)
		}
		if pass == "" {
			// As above: a presented-but-empty password is a client bug worth seeing, not
			// the protocol's own credential-less first CONNECT.
			return "", smokescreen.MissingRoleError("empty token in Basic Proxy-Authorization")
		}
		return validate(pass)
	}
}

func roleFromRequest(mode string) func(*http.Request) (string, error) {
	switch mode {
	case "", "netid":
		return withRoleErrorDetail(newNetIDRole())
	case "jwt":
		return withRoleErrorDetail(newJWTRole())
	case "basic-name":
		return withRoleErrorDetail(newBasicNameRole())
	case "basic-jwt":
		return withRoleErrorDetail(newBasicJWTRole())
	default:
		logrus.Fatalf("unknown SMOKESCREEN_ID_MODE=%q", mode)
		return nil
	}
}

// withRoleErrorDetail carries *why* identity could not be established from the role func to
// the reject handler, which is the only place that can put it on the decision row before
// smokescreen logs it. Smokescreen flattens every identity failure to the same opaque
// "Client role cannot be determined", and the real cause — expired token, wrong audience,
// unmapped subnet — otherwise survives only in a separate syslog line you'd have to join
// against.
//
// The request is the carrier because it is the one object both halves hold. The wrapper
// writes the header on failure and *deletes* it on success, so the value is always ours: a
// client that sends this header itself can never have it read back as a proxy verdict.
func withRoleErrorDetail(next func(*http.Request) (string, error)) func(*http.Request) (string, error) {
	return func(req *http.Request) (string, error) {
		role, err := next(req)
		if err != nil {
			req.Header.Set(roleErrorHeader, err.Error())
		} else {
			req.Header.Del(roleErrorHeader)
		}
		return role, err
	}
}

// takeRoleErrorDetail reads and clears the detail set by withRoleErrorDetail. Clearing it
// keeps the internal header off any request that goes on to be proxied.
func takeRoleErrorDetail(req *http.Request) string {
	if req == nil {
		return ""
	}
	detail := req.Header.Get(roleErrorHeader)
	req.Header.Del(roleErrorHeader)
	return detail
}

// isBasicMode reports whether the identity mode carries credentials in Basic proxy auth
// and therefore needs the 407 Basic challenge.
func isBasicMode(mode string) bool {
	return mode == "basic-name" || mode == "basic-jwt"
}

// newRejectHandler builds the handler smokescreen calls on every rejection. It does two
// things, both of which need the one moment where the request and the pending decision are
// both in hand:
//
//  1. In the Basic modes it turns a credential-less 407 into a real auth challenge by adding
//     `Proxy-Authenticate: Basic realm="egress"`. This is the linchpin for .NET clients:
//     HttpClient only attaches DefaultProxyCredentials to the CONNECT *after* it sees this
//     header on a 407. We challenge only when no Proxy-Authorization was presented; a request
//     that DID present creds but was still denied gets a plain 407 with no re-challenge, so
//     clients don't loop. That same test — were credentials presented at all? — separates
//     handshake noise from a real auth failure, so it also sets the pre-auth log marker.
//  2. It replaces smokescreen's opaque "Client role cannot be determined" with what actually
//     happened: either the challenge, or the role func's own error (see withRoleErrorDetail).
//
// Ordering is load-bearing and comes from smokescreen: HandleConnectFunc *defers* logProxy,
// so this handler runs first and both the marker and the reason are in place by the time the
// decision line is formatted. sctx is mutated exactly as smokescreen's own rejectResponse does.
func newRejectHandler(mode string) func(*smokescreen.SmokescreenContext, *http.Response) {
	basic := isBasicMode(mode)
	return func(sctx *smokescreen.SmokescreenContext, resp *http.Response) {
		detail := takeRoleErrorDetail(resp.Request)
		if resp.StatusCode != http.StatusProxyAuthRequired {
			return
		}
		credentialLess := resp.Request == nil || resp.Request.Header.Get("Proxy-Authorization") == ""

		switch {
		case basic && credentialLess:
			resp.Header.Set("Proxy-Authenticate", `Basic realm="egress"`)
			if sctx != nil && sctx.Logger != nil {
				sctx.Logger = sctx.Logger.WithField(logFieldPreAuth, true)
			}
			setDecisionReason(sctx, "No proxy credentials presented; answered with a 407 Basic challenge")
		case detail != "":
			setDecisionReason(sctx, "Client identity rejected: "+detail)
		}
	}
}

// setDecisionReason replaces smokescreen's reason on the pending decision. Safe because
// logProxy reads sctx.Decision.Reason after this handler returns, while the 407 body was
// already rendered from the original text.
func setDecisionReason(sctx *smokescreen.SmokescreenContext, reason string) {
	if sctx != nil && sctx.Decision != nil {
		sctx.Decision.Reason = reason
	}
}

// preAuthEventHook relabels the marked decision row as CANONICAL-PROXY-AUTH-REQUIRED and
// drops it to info: a bare CONNECT is protocol noise, not a verdict. Everything else passes
// through untouched — in particular a CONNECT that presented credentials which failed
// validation stays a CANONICAL-PROXY-DECISION denial, which is the signal worth alerting on.
// Logrus dups the entry before firing hooks, so both mutations are scoped to this one line.
type preAuthEventHook struct{}

func (preAuthEventHook) Levels() []logrus.Level { return logrus.AllLevels }

func (preAuthEventHook) Fire(e *logrus.Entry) error {
	if _, marked := e.Data[logFieldPreAuth]; !marked {
		return nil
	}
	delete(e.Data, logFieldPreAuth)
	if e.Message == smokescreen.CanonicalProxyDecision {
		e.Message = canonicalProxyAuthRequired
		e.Level = logrus.InfoLevel
	}
	return nil
}

// preAuthNoiseFilter drops smokescreen's per-request "Unable to get role for request" error
// line for the credential-less case only. It carries nothing the AUTH-REQUIRED row doesn't
// (its error text is the constant missingBasicCredsMsg), and being non-canonical it lands in
// the Syslog diagnostic stream — one shipped row per tunnel, for a protocol formality.
// Anything else, including a rejected token, is formatted normally: this filter must never
// be the reason a failed authentication goes unrecorded. Set LOG_PREAUTH_DETAIL=1 to keep it.
type preAuthNoiseFilter struct{ inner logrus.Formatter }

func (f *preAuthNoiseFilter) Format(e *logrus.Entry) ([]byte, error) {
	if isPreAuthRoleError(e) {
		return nil, nil
	}
	return f.inner.Format(e)
}

// isPreAuthRoleError reports whether an entry is the role-error line for a CONNECT that
// presented no credentials at all. Deliberately narrow: message, missing-role flag and the
// exact error text must all match, so a validation failure never matches.
func isPreAuthRoleError(e *logrus.Entry) bool {
	if e.Message != roleErrorMsg {
		return false
	}
	if missing, ok := e.Data["is_missing_role"].(bool); !ok || !missing {
		return false
	}
	err, ok := e.Data["error"].(error)
	return ok && err.Error() == missingBasicCredsMsg
}

// applyJSONLogging routes both smokescreen's logrus and the stdlib log through structured
// JSON, so the audit trail is machine-parseable. Shared by standalone and managed modes.
func applyJSONLogging(conf *smokescreen.Config) {
	var formatter logrus.Formatter = &logrus.JSONFormatter{}
	if !envEnabled("LOG_PREAUTH_DETAIL") {
		formatter = &preAuthNoiseFilter{inner: formatter}
	}
	conf.Log.Formatter = formatter
	conf.Log.AddHook(preAuthEventHook{})
	adapter := &smokescreen.Log2LogrusWriter{Entry: conf.Log.WithField("stdlog", "1")}
	log.SetOutput(adapter)
	log.SetFlags(0)
}

// envEnabled reads a boolean opt-in from the environment. Unset means off, so every knob
// spelled this way defaults to the quieter/safer behaviour.
func envEnabled(name string) bool {
	switch strings.ToLower(strings.TrimSpace(os.Getenv(name))) {
	case "1", "true", "yes", "on":
		return true
	}
	return false
}

func main() {
	// Managed mode: the allowlist watch/render/reload loop is folded into this binary
	// (no sidecars, no shell supervisor). Triggered by an allowlist blob URL (managed
	// identity, on Azure) or a blob connection string (local docker / Azurite).
	if os.Getenv("ALLOWLIST_BLOB_URL") != "" || os.Getenv("ALLOWLIST_BLOB_CONNECTION_STRING") != "" {
		runManaged()
		return
	}

	conf, err := cmd.NewConfiguration(nil, nil)
	if err != nil {
		logrus.Fatalf("could not create configuration: %v", err)
	} else if conf != nil {
		mode := os.Getenv("SMOKESCREEN_ID_MODE")
		conf.RoleFromRequest = roleFromRequest(mode)
		// Spells out why a request was rejected, and in the Basic identity models emits the
		// challenge on credential-less CONNECTs so clients attach their proxy credentials.
		conf.RejectResponseHandlerWithCtx = newRejectHandler(mode)
		applyJSONLogging(conf)
		smokescreen.StartWithConfig(conf, nil)
	}
	// else: --help/--version handled inside NewConfiguration
}
