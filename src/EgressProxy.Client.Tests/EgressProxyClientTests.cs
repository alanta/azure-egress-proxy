using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace EgressProxy.Client.Tests;

public sealed class EgressProxyClientTests
{
    [Fact]
    public void CredentialsReturnClientIdAndTokenForBasic()
    {
        var now = DateTimeOffset.UtcNow;
        var tokenCredential = new QueueTokenCredential(
            new AccessToken("token-1", now.AddMinutes(20)));
        var credentials = new EgressProxyCredentials("client-id", "api://egress", tokenCredential);

        var networkCredential = credentials.GetCredential(new Uri("https://proxy"), "Basic");

        Assert.NotNull(networkCredential);
        Assert.Equal("client-id", networkCredential.UserName);
        Assert.Equal("token-1", networkCredential.Password);
    }

    [Fact]
    public void CredentialsCacheTokenUntilNearExpiryThenRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        var current = now;
        var tokenCredential = new QueueTokenCredential(
            new AccessToken("token-1", now.AddMinutes(10)),
            new AccessToken("token-2", now.AddMinutes(30)));

        var credentials = new EgressProxyCredentials(
            "client-id",
            "api://egress",
            tokenCredential,
            () => current);

        var first = credentials.GetCredential(new Uri("https://proxy"), "Basic");
        var second = credentials.GetCredential(new Uri("https://proxy"), "Basic");
        current = now.AddMinutes(9);
        var third = credentials.GetCredential(new Uri("https://proxy"), "Basic");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal("token-1", first.Password);
        Assert.Equal("token-1", second.Password);
        Assert.Equal("token-2", third.Password);
        Assert.Equal(2, tokenCredential.RequestCount);
    }

    [Fact]
    public void BuildRegistrationReturnsNullWhenProxyEnvironmentIsUnset()
    {
        var registration = EgressProxyServiceCollectionExtensions.BuildRegistration(
            new EgressProxyOptions { Audience = "api://egress", ClientId = "client-id" },
            _ => null);

        Assert.Null(registration);
    }

    [Fact]
    public void NoProxyBypassesListedHost()
    {
        var registration = EgressProxyServiceCollectionExtensions.BuildRegistration(
            new EgressProxyOptions
            {
                Audience = "api://egress",
                ClientId = "client-id",
                TokenCredential = new QueueTokenCredential(new AccessToken("token-1", DateTimeOffset.UtcNow.AddMinutes(20)))
            },
            name => name switch
            {
                "HTTPS_PROXY" => "http://proxy.local:4750",
                "NO_PROXY" => "api.github.com,.internal.local",
                _ => null
            });

        Assert.NotNull(registration);
        Assert.True(registration.Proxy.IsBypassed(new Uri("https://api.github.com")));
        Assert.True(registration.Proxy.IsBypassed(new Uri("https://service.internal.local")));
        Assert.False(registration.Proxy.IsBypassed(new Uri("https://example.org")));
    }

    [Fact]
    public void AddEgressProxyKeepsFactoryWorkingWhenProxyUnset()
    {
        var originalHttpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var originalHttpsProxyLower = Environment.GetEnvironmentVariable("https_proxy");
        var originalNoProxy = Environment.GetEnvironmentVariable("NO_PROXY");
        var originalNoProxyLower = Environment.GetEnvironmentVariable("no_proxy");

        try
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
            Environment.SetEnvironmentVariable("https_proxy", null);
            Environment.SetEnvironmentVariable("NO_PROXY", null);
            Environment.SetEnvironmentVariable("no_proxy", null);

            var services = new ServiceCollection();
            services.AddHttpClient();
            services.AddEgressProxy(options =>
            {
                options.Audience = "api://egress";
                options.ClientId = "client-id";
            });

            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient();

            Assert.NotNull(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", originalHttpsProxy);
            Environment.SetEnvironmentVariable("https_proxy", originalHttpsProxyLower);
            Environment.SetEnvironmentVariable("NO_PROXY", originalNoProxy);
            Environment.SetEnvironmentVariable("no_proxy", originalNoProxyLower);
        }
    }

    private sealed class QueueTokenCredential(params AccessToken[] tokens) : TokenCredential
    {
        private readonly Queue<AccessToken> _tokens = new(tokens);

        public int RequestCount { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (_tokens.Count == 0)
            {
                throw new InvalidOperationException("No tokens left to return.");
            }

            return _tokens.Dequeue();
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
