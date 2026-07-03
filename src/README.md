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
