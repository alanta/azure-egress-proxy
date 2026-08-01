namespace Portal.Tests;

/// <summary>
/// The console is on nobody's critical path, and this is where that is pinned rather than
/// asserted in prose. The proxy polls the allowlist blob and a pipeline pushes to the
/// control-plane API; neither has heard of the console, so a console outage costs the platform
/// team its view and costs enforcement and policy writes nothing.
///
/// These are dependency-direction tests. They cannot prove the deployment stays up — that is the
/// local-stack demonstration in the change's task 13.4 — but they are what would fail first if a
/// later change quietly made something wait on the console.
/// </summary>
public class OutageTests
{
    /// <summary>
    /// Nothing builds against the portal except its own tests and the Aspire AppHost, which
    /// launches it locally and is not part of any deployment. A project reference is the cheapest
    /// possible way to make the control plane's build depend on the console, and the first step
    /// toward a runtime dependency.
    /// </summary>
    [Fact]
    public void Only_the_portals_own_tests_and_the_local_apphost_reference_the_portal()
    {
        var referencing = Repo.Files("src", "*.csproj")
            .Where(file => !file.Contains("/obj/", StringComparison.Ordinal))
            .Where(file => Repo.ReadText(file).Replace('\\', '/')
                .Contains("Portal/Portal.csproj", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["src/AppHost/AppHost.csproj", "src/Portal.Tests/Portal.Tests.csproj"], referencing);
    }

    /// <summary>
    /// Neither the proxy nor the control plane carries a setting that points at the console.
    /// Configuration keys in this repo are upper-case environment variables, so the absence of the
    /// word is the absence of the wire — and unlike a scan of prose, it cannot be tripped by a
    /// comment explaining the relationship.
    /// </summary>
    [Theory]
    [InlineData("proxy", "*.go")]
    [InlineData("src/ControlPlane", "*.cs")]
    public void The_enforcement_and_write_paths_hold_no_address_for_the_console(
        string directory, string pattern)
    {
        var offenders = Repo.Files(directory, pattern)
            .Where(file => !file.Contains("/obj/", StringComparison.Ordinal))
            .Where(file => Repo.ReadText(file).Contains("PORTAL", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "a PORTAL-shaped setting appeared in " + string.Join(", ", offenders)
            + ". The console reads those services; they must not read it.");
    }

    /// <summary>
    /// The same direction in the local stack: the console waits for the mock IdP and Azurite, and
    /// nothing waits for the console. Aspire's <c>WaitFor</c> is the local equivalent of a startup
    /// dependency, so this is where an accidental one would show up first.
    /// </summary>
    [Fact]
    public void Nothing_in_the_local_stack_waits_for_the_console()
    {
        var appHost = Repo.ReadText("src/AppHost/AppHost.cs");

        Assert.Contains("AddProject<Projects.Portal>(\"portal\")", appHost, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitFor(portal", appHost, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForCompletion(portal", appHost, StringComparison.Ordinal);
    }
}
