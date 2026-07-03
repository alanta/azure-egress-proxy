using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Configuration;

namespace ServiceDefaults;

public static class EgressProxyLocalTokenCredential
{
    public static TokenCredential? CreateFromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var tokenEndpoint = configuration["EgressProxy:TokenEndpoint"];
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            return null;
        }

        var appId = configuration["EgressProxy:ClientId"];
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new InvalidOperationException("EgressProxy:ClientId is required when EgressProxy:TokenEndpoint is set.");
        }

        return new MockManagedIdentityTokenCredential(new Uri(tokenEndpoint), appId);
    }

    private sealed class MockManagedIdentityTokenCredential(Uri tokenEndpoint, string appId) : TokenCredential
    {
        private readonly HttpClient _httpClient = new();
        private readonly Uri _tokenRequestUri = BuildTokenUri(tokenEndpoint, appId);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => GetTokenCoreAsync(cancellationToken).GetAwaiter().GetResult();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => GetTokenCoreAsync(cancellationToken);

        private async ValueTask<AccessToken> GetTokenCoreAsync(CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(_tokenRequestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var token = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Mock IdP returned an empty token.");
            }

            return new AccessToken(token, ReadExpiry(token));
        }

        private static Uri BuildTokenUri(Uri tokenEndpoint, string appId)
        {
            var separator = tokenEndpoint.Query.Length == 0 ? "?" : "&";
            return new Uri($"{tokenEndpoint}{separator}appid={Uri.EscapeDataString(appId)}");
        }

        private static DateTimeOffset ReadExpiry(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                throw new InvalidOperationException("Mock IdP token is not a valid JWT.");
            }

            var payloadBytes = Base64UrlDecode(parts[1]);
            using var payload = JsonDocument.Parse(payloadBytes);
            if (!payload.RootElement.TryGetProperty("exp", out var expElement))
            {
                throw new InvalidOperationException("Mock IdP token payload does not contain exp.");
            }

            long expUnix;
            if (expElement.ValueKind == JsonValueKind.Number)
            {
                expUnix = expElement.GetInt64();
            }
            else if (expElement.ValueKind == JsonValueKind.String &&
                     long.TryParse(expElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                expUnix = parsed;
            }
            else
            {
                throw new InvalidOperationException("Mock IdP token exp claim is invalid.");
            }

            return DateTimeOffset.FromUnixTimeSeconds(expUnix);
        }

        private static byte[] Base64UrlDecode(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            var padding = normalized.Length % 4;
            if (padding is 2)
            {
                normalized += "==";
            }
            else if (padding is 3)
            {
                normalized += "=";
            }
            else if (padding is 1)
            {
                throw new InvalidOperationException("Mock IdP token payload is not valid Base64Url.");
            }

            return Convert.FromBase64String(normalized);
        }
    }
}
