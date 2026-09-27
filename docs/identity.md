# Workload identity — the managed-identity JWT in the Basic proxy password

The proxy must know **which workload** is asking before it can apply a per-workload
allowlist. Network position (source IP/subnet) is not a trustworthy signal — services
can't be pinned to subnets, and shared Container Apps environments put unrelated apps in
one subnet. So the workload proves its identity with the one credential it already has:
its **Entra managed-identity access token**.

## The mechanism (`SMOKESCREEN_ID_MODE=basic-jwt`)

The token has to arrive **on the `CONNECT` request itself** — anything inside the TLS
tunnel is invisible to the proxy. .NET (and most runtimes) cannot put a custom
`Proxy-Authorization: Bearer` header on the CONNECT without hand-rolled socket code, but
they *do* natively attach **Basic proxy credentials** after a
`407 Proxy-Authenticate: Basic` challenge. So:

1. Client CONNECTs without credentials → proxy answers `407` with
   `Proxy-Authenticate: Basic realm="egress"` (only on credential-less requests, so
   denied-with-creds responses never loop). That step is audited as its own event,
   `CANONICAL-PROXY-AUTH-REQUIRED` — see [observability.md](observability.md).
2. Client retries with `Proxy-Authorization: Basic base64("<appid>:<MI access token>")` —
   the token rides in the **password**; the username is informational.
3. Proxy validates the token exactly like a Bearer token — **RS256 signature via JWKS,
   issuer, audience, expiry** (60 s leeway) — and takes the workload identity from the
   `appid` claim (Entra v1 / managed-identity tokens) or `azp` (v2 tokens).
4. That client ID is the **role**, matched against `modules[].appid` in the
   [allowlist](allowlist.md). Auth happens **once per tunnel**, not per request; the
   token rotates naturally on reconnect.

Security is equivalent to a Bearer token: the credential is a signed, short-lived JWT a
compromised neighbour cannot mint for another identity. The client cost is a few lines —
an `ICredentials` returning `NetworkCredential(appid, token)` assigned to
`HttpClientHandler.DefaultProxyCredentials` (shipped here as `EgressProxy.Client`).

## Proxy configuration

| Variable | v2 tokens (recommended) | v1 / raw MI tokens |
|---|---|---|
| `JWKS_URL` | `https://login.microsoftonline.com/<tenant>/discovery/v2.0/keys` | same |
| `EXPECT_ISS` | `https://login.microsoftonline.com/<tenant>/v2.0` | `https://sts.windows.net/<tenant>/` |
| `EXPECT_AUD` | the proxy app registration's **client ID** (GUID) | the App ID URI, e.g. `api://egress-proxy` |

The token version is decided by the **proxy's app registration**
(`accessTokenAcceptedVersion`), created once by `scripts/setup-identity.sh`. The workload
requests its token for `"<EXPECT_AUD>/.default"` — the resource the token is *for* is the
proxy's app registration, and the identity *in* it is the workload's own client ID.
Locally, the mock IdP ([`mock-idp/`](../mock-idp/)) stands in for the token endpoint and
JWKS; no Entra needed.

### Startup check

Before it loads keys, polls the allowlist, or opens a port, the proxy checks the identity
configuration, in managed and standalone mode alike:

| Key | Required | Rule |
|---|---|---|
| `SMOKESCREEN_ID_MODE` | always | one of `basic-jwt`, `jwt`, `basic-name`, `netid`, spelled exactly. There is **no default**: `netid` is used only when named |
| `JWKS_URL` | `jwt`, `basic-jwt` | an absolute `https` URL with a host. Keys fetched over plain `http` can be replaced in transit |
| `EXPECT_ISS` | `jwt`, `basic-jwt` | non-empty. An empty issuer switches golang-jwt's issuer check off, and since Entra signing keys are shared across tenants, it switches the tenant check off with it: any Entra token for the right audience would pass |
| `EXPECT_AUD` | `jwt`, `basic-jwt` | non-empty. An empty audience rejects every token as "invalid audience", which hides the real cause |
| `SUBNET_ROLES` | `netid`, standalone only | parses as `cidr=role,...`. Managed `netid` takes its subnets from `modules[].subnet` |

If anything fails, the proxy logs **every** failing key with its reason in a single `fatal`
line and exits with a non-zero status, for example:

```text
level=fatal msg="invalid identity configuration, refusing to start: JWKS_URL: \"http://idp/keys\" uses http; it must be https, because signing keys fetched over plain http can be replaced in transit (JWKS_ALLOW_INSECURE_HTTP=1 allows http for local development only); EXPECT_ISS: not set; without it the token issuer, and with it the tenant, is not checked; EXPECT_AUD: not set; every token would be rejected as having the wrong audience" invalid_config="[JWKS_URL EXPECT_ISS EXPECT_AUD]"
```

It does **not** fall back to a deny-all listener. A misconfigured identity is not a policy
gap the allowlist's fail-closed path can cover (see [allowlist.md](allowlist.md) § Fail
closed): an open port passes the TCP health probe, so the instance would look healthy to the
load balancer and to automatic OS upgrades while authenticating no one, or authenticating
too many. Under systemd (`Restart=always`) each restart fails the same way, with the same
log line, and the instance never reports healthy until the configuration is fixed.

**Local development only:** the Aspire stack's mock IdP serves its JWKS over plain `http`.
`JWKS_ALLOW_INSECURE_HTTP=1` lets `JWKS_URL` use `http`. It is off unless set, relaxes the
scheme and nothing else (the URL must still be well-formed, the issuer and audience are
still required), and logs a warning at every start. `src/AppHost` sets it; no deployment
template does, and none should.

### Signing keys

In `jwt` and `basic-jwt` mode the proxy **does not start serving until it holds signing
keys**. At startup it fetches `JWKS_URL` up to 30 times, a second apart. An unreachable
URL, a non-200 response, bad JSON, or a key set with no usable keys each count as a
failure. If every attempt fails, the process exits with the last error. Without this, the
proxy would reject every token while its open port still passed the health probe. Once
keys are loaded, the proxy refreshes them hourly, and at most every 5 minutes when a token
carries an unknown key ID. A failed refresh keeps the cached keys. In managed mode an
allowlist reload reuses the loaded keys, so a policy push never waits on the JWKS.

## Other modes (supported, situational)

| Mode | Identity | Trust | Use |
|---|---|---|---|
| `basic-jwt` | validated MI JWT in Basic password | strong | **the deployed design** |
| `basic-name` | the Basic **username**, as-is | spoofable | bootstrap/low-trust; ~1 line of client config |
| `jwt` | MI JWT in `Proxy-Authorization: Bearer` | strong | runtimes that can set CONNECT headers (Go, curl) |
| `netid` | source subnet → role (`SUBNET_ROLES` or `modules[].subnet`) | network-bound | infrastructure clients pinned to a subnet |

All modes return a **role** that must match a module in the allowlist; an empty/invalid
identity lands on the fallback/deny block.

The mode is always named explicitly (see [Startup check](#startup-check)). An unset
`SMOKESCREEN_ID_MODE` used to mean `netid`, and in managed mode so did a misspelled one,
which silently swapped JWT identity for network position. Both are now a startup failure.
