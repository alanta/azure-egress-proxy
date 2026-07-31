namespace Portal.Tests;

/// <summary>
/// The portal is read-only, and this is where that stops being a promise in a document.
///
/// A runtime test can only prove that the paths a test happens to exercise issue no write; these
/// read the portal's own source, so a write added to a surface nobody wrote a test for still
/// fails the build. Crude on purpose — the property being defended (an admin console for a
/// security control cannot change policy) is worth a blunt instrument.
/// </summary>
public class ReadOnlyTests
{
    /// <summary>
    /// The only non-GET the portal may issue to the control plane is <c>:check</c>, which writes
    /// nothing. `PUT /rulesets/{name}` and `DELETE /rulesets/{name}` are the two verbs that change
    /// policy, and the portal must reach neither: an operator applies a change through the
    /// existing audited machine path, from the snippet the console renders (design.md D3).
    /// </summary>
    [Theory]
    [InlineData("HttpMethod.Put")]
    [InlineData("HttpMethod.Delete")]
    [InlineData("PutAsync")]
    [InlineData("PutAsJsonAsync")]
    [InlineData("DeleteAsync")]
    public void The_portal_never_issues_a_write_verb(string forbidden)
    {
        var offenders = PortalSources()
            .Where(file => Code(file).Contains(forbidden, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{forbidden}' appears in {string.Join(", ", offenders)}. The portal writes nothing: "
            + "its only non-GET call to the control plane is POST /rulesets/{name}:check.");
    }

    /// <summary>
    /// The one permitted non-GET, asserted positively so that removing the read-only rule above
    /// cannot quietly take the dry-run path with it. `:check` runs the same validation as a write
    /// and stops before the blob — the API guarantees it, and the portal depends on that.
    /// </summary>
    [Fact]
    public void The_only_permitted_write_shaped_call_is_the_dry_run()
    {
        var posts = PortalSources()
            .SelectMany(file => Code(file)
                .Split('\n')
                .Where(line => line.Contains("HttpMethod.Post", StringComparison.Ordinal)
                    || line.Contains("PostAsync", StringComparison.Ordinal)
                    || line.Contains("PostAsJsonAsync", StringComparison.Ordinal))
                .Select(line => (file, line: line.Trim())))
            .ToList();

        Assert.NotEmpty(posts);
        Assert.All(posts, p => Assert.True(
            p.line.Contains(":check", StringComparison.Ordinal),
            $"unexpected POST in {p.file}: {p.line}. The dry run is the only one permitted."));
    }

    /// <summary>
    /// Source with comments removed. The scan is looking for calls, and the surrounding prose
    /// says things like "there is no PutAsync to call" — which is the rule being documented, not
    /// a violation of it. Blunt but sufficient: no string literal in this codebase contains a
    /// comment marker.
    /// </summary>
    private static string Code(string file)
    {
        var text = Repo.ReadText(file);

        // Razor block comments first: they span lines, so a line filter cannot see them.
        while (text.IndexOf("@*", StringComparison.Ordinal) is var open and >= 0
            && text.IndexOf("*@", open, StringComparison.Ordinal) is var close and > 0)
        {
            text = text.Remove(open, close - open + 2);
        }

        return string.Join('\n', text.Split('\n').Select(line =>
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment >= 0 ? line[..comment] : line;
        }));
    }

    /// <summary>
    /// C# and Razor under src/Portal, excluding the vendored htmx (minified third-party code that
    /// naturally contains every HTTP verb) and the build outputs.
    /// </summary>
    private static IEnumerable<string> PortalSources() =>
        new[] { "*.cs", "*.cshtml" }
            .SelectMany(pattern => Repo.Files("src/Portal", pattern))
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal)
                && !path.Contains("/bin/", StringComparison.Ordinal));
}
