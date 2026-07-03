using Azure.Core;
using System.Net;

namespace EgressProxy.Client;

/// <summary>
/// Provides Basic proxy credentials using a managed-identity access token.
/// </summary>
public sealed class EgressProxyCredentials : ICredentials
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly string _clientId;
    private readonly TokenCredential _tokenCredential;
    private readonly TokenRequestContext _tokenRequestContext;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _cacheLock = new();
    private AccessToken? _cachedToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="EgressProxyCredentials"/> class.
    /// </summary>
    /// <param name="clientId">Managed identity client ID used as Basic auth username.</param>
    /// <param name="audience">The proxy audience. The token scope is "{audience}/.default".</param>
    /// <param name="tokenCredential">Token credential used to acquire managed-identity access tokens.</param>
    /// <param name="utcNow">Optional UTC clock for tests.</param>
    /// <exception cref="ArgumentException">Thrown when required arguments are missing.</exception>
    public EgressProxyCredentials(
        string clientId,
        string audience,
        TokenCredential tokenCredential,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client ID is required.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new ArgumentException("Audience is required.", nameof(audience));
        }

        _clientId = clientId;
        _tokenCredential = tokenCredential ?? throw new ArgumentNullException(nameof(tokenCredential));
        _tokenRequestContext = new TokenRequestContext([$"{audience.TrimEnd('/')}/.default"]);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public NetworkCredential? GetCredential(Uri? uri, string? authType)
    {
        if (!string.IsNullOrEmpty(authType) && !authType.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = GetValidToken();
        return new NetworkCredential(_clientId, token);
    }

    internal string GetValidToken()
    {
        var now = _utcNow();
        var cachedToken = _cachedToken;
        if (cachedToken.HasValue && IsTokenFresh(cachedToken.Value, now))
        {
            return cachedToken.Value.Token;
        }

        lock (_cacheLock)
        {
            now = _utcNow();
            cachedToken = _cachedToken;
            if (cachedToken.HasValue && IsTokenFresh(cachedToken.Value, now))
            {
                return cachedToken.Value.Token;
            }

            var newToken = _tokenCredential.GetToken(_tokenRequestContext, CancellationToken.None);
            _cachedToken = newToken;
            return newToken.Token;
        }
    }

    private static bool IsTokenFresh(AccessToken accessToken, DateTimeOffset now)
        => accessToken.ExpiresOn > now.Add(RefreshSkew);
}
