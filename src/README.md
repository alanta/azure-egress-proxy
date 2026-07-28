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

Both legs of a CONNECT tunnel cross an Azure idle timer (4 min by default). The handler is
configured so tunnels never outlive it:

| Option | Default | Why |
|---|---|---|
| `PooledConnectionIdleTimeout` | 1 min | Retire idle pooled tunnels before Azure reaps them; values ≥ 4 min are rejected at startup. |
| `TcpKeepAliveTime` | 30 s | Keeps in-use-but-quiet tunnels (SSE, long-poll, gRPC streaming) alive, and surfaces a dead one in ~15 s. `TimeSpan.Zero` disables. |

Raise these only alongside `proxyIdleTimeoutInMinutes` in the infrastructure. Full rationale,
including what non-.NET clients must configure:
[docs/production-hardening.md § Idle timeouts](../docs/production-hardening.md#idle-timeouts--the-stale-tunnel-contract).
