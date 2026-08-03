using System.Text.Json;

namespace Portal.Auth;

/// <summary>
/// Reads the principal that Azure Container Apps' built-in authentication ("EasyAuth") injects
/// after it has completed the OIDC flow against Entra at the platform edge.
///
/// Why the platform rather than an in-app OIDC library: the portal ships zero authentication
/// dependencies this way, which matters because SECURITY_GUIDELINES.md makes every package a
/// reviewed change, and swapping the identity provider becomes container-app configuration plus
/// one implementation of <see cref="IPortalUserAccessor"/> — which is exactly the door design.md
/// D1 exists to hold open.
///
/// <para><b>This class trusts its input.</b> The headers below are only trustworthy because the
/// auth sidecar strips any client-supplied copy before the container sees the request, and
/// because ingress reaches the app through that sidecar and nowhere else. Two consequences that
/// must not be quietly undone: <c>unauthenticatedClientAction</c> stays
/// <c>RedirectToLoginPage</c> in the Bicep, and nothing may be placed in front of the app that
/// forwards these headers from outside.</para>
/// </summary>
public sealed class ContainerAppsUserAccessor(IHttpContextAccessor accessor) : IPortalUserAccessor
{
    /// <summary>Base64 of a JSON principal. The full shape; used for the display name.</summary>
    internal const string PrincipalHeader = "X-MS-CLIENT-PRINCIPAL";

    /// <summary>The subject claim, pre-extracted by the sidecar.</summary>
    internal const string PrincipalIdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    /// <summary>Usually the UPN. A convenience the sidecar provides; not always populated.</summary>
    internal const string PrincipalNameHeader = "X-MS-CLIENT-PRINCIPAL-NAME";

    public PortalUser? Current
    {
        get
        {
            if (accessor.HttpContext?.Request.Headers is not { } headers)
            {
                return null;
            }

            var subject = headers[PrincipalIdHeader].ToString();
            if (string.IsNullOrWhiteSpace(subject))
            {
                return null;
            }

            var name = headers[PrincipalNameHeader].ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = NameFromPrincipal(headers[PrincipalHeader].ToString()) ?? subject;
            }

            return new PortalUser(subject, name);
        }
    }

    /// <summary>
    /// Digs the display name out of the encoded principal. Best-effort by design: a malformed or
    /// truncated header degrades to the subject rather than failing the request, because a header
    /// the app cannot parse is a cosmetic problem — authentication already happened upstream.
    /// </summary>
    private static string? NameFromPrincipal(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            if (!document.RootElement.TryGetProperty("claims", out var claims)
                || claims.ValueKind is not JsonValueKind.Array)
            {
                return null;
            }

            string? Claim(string type)
            {
                foreach (var claim in claims.EnumerateArray())
                {
                    if (claim.TryGetProperty("typ", out var t)
                        && string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase)
                        && claim.TryGetProperty("val", out var value))
                    {
                        return value.GetString();
                    }
                }

                return null;
            }

            return Claim("name") ?? Claim("preferred_username");
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }
}
