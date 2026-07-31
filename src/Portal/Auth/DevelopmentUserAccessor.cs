namespace Portal.Auth;

/// <summary>
/// A fixed operator, for the Aspire loop only. The local stack has no auth sidecar in front of
/// the portal, and standing up a mock OIDC provider would test the mock rather than the portal —
/// what the local loop is for is the console over Azurite, the mock IdP, and the control plane.
///
/// <para><b>Registered only when the environment is Development</b> (see Program.cs). The
/// production path is <see cref="ContainerAppsUserAccessor"/>, which returns null when the
/// platform has not authenticated the request. If this type is ever reachable in Azure the portal
/// has no authentication at all, so the selection is a hard environment check with no
/// configuration switch to get wrong.</para>
/// </summary>
public sealed class DevelopmentUserAccessor : IPortalUserAccessor
{
    public PortalUser? Current { get; } = new("local-operator", "Local operator (development)");
}
