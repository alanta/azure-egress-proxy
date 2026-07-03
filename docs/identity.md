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
   denied-with-creds responses never loop).
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

## Other modes (supported, situational)

| Mode | Identity | Trust | Use |
|---|---|---|---|
| `basic-jwt` | validated MI JWT in Basic password | strong | **the deployed design** |
| `basic-name` | the Basic **username**, as-is | spoofable | bootstrap/low-trust; ~1 line of client config |
| `jwt` | MI JWT in `Proxy-Authorization: Bearer` | strong | runtimes that can set CONNECT headers (Go, curl) |
| `netid` | source subnet → role (`SUBNET_ROLES` or `modules[].subnet`) | network-bound | infrastructure clients pinned to a subnet |

All modes return a **role** that must match a module in the allowlist; an empty/invalid
identity lands on the fallback/deny block.
