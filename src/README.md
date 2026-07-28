# .NET client and sample workload

`EgressProxy.Client` wires `IHttpClientFactory` clients to use `HTTPS_PROXY` with managed-identity Basic credentials (`clientId:token`) for `SMOKESCREEN_ID_MODE=basic-jwt`.

Add this to your app:

```csharp
builder.Services.AddHttpClient();
builder.Services.AddEgressProxy(options =>
{
    options.Audience = "<proxy-app-client-id-or-app-id-uri>";
    options.ClientId = builder.Configuration["EgressProxy:ClientId"];
});
```

## Idle tunnels

Both legs of a CONNECT tunnel cross an Azure idle timer (4 min by default), and the proxy
itself closes an idle client connection at 300 s. The handler is configured so pooled tunnels
never outlive either:

| Option | Default | Why |
|---|---|---|
| `PooledConnectionIdleTimeout` | 1 min | Retire idle pooled tunnels before the proxy or the platform closes them; values ≥ 4 min are rejected at startup. |

The client deliberately does *not* set its own TCP keepalives: the proxy already keepalives
both legs, which is what keeps the Azure idle timers from firing at all. Full rationale, the
measurements behind it, and what non-.NET clients must configure:
[docs/production-hardening.md § Idle timeouts](../docs/production-hardening.md#idle-timeouts--the-stale-tunnel-contract).
