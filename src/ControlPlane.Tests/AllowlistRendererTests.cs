using ControlPlane.Model;

namespace ControlPlane.Tests;

public class AllowlistRendererTests
{
    /// <summary>
    /// The load-bearing test for the whole "no proxy change" claim: the committed ruleset store
    /// renders to the committed allowlist document, byte for byte. If a change to the ruleset model
    /// ever perturbs the rendered shape, this fails here rather than in the proxy at runtime.
    /// </summary>
    [Fact]
    public void Rendering_the_committed_store_reproduces_the_committed_allowlist()
    {
        var state = StateJson.Deserialize<StateDocument>(Repo.ReadText("allowlist/rulesets.json"));

        var rendered = StateJson.Serialize(AllowlistRenderer.Render(state));

        Assert.Equal(Repo.ReadText("allowlist/allowlist.json").TrimEnd(), rendered.TrimEnd());
    }

    /// <summary>
    /// A ruleset governs one or more clients, but the proxy keys each ACL entry on a single
    /// identity — so several subjects fan out into one entry each, all carrying the same content.
    /// </summary>
    [Fact]
    public void A_multi_subject_ruleset_renders_one_entry_per_subject()
    {
        var state = new StateDocument
        {
            Rulesets =
            [
                new Ruleset
                {
                    Name = "payments",
                    Subjects =
                    [
                        new Subject { Appid = "11111111-1111-1111-1111-111111111111" },
                        new Subject { Netid = "10.2.0.0/23" },
                    ],
                    Content = new RulesetContent { AllowedHosts = ["api.stripe.com"], Action = "enforce" },
                },
            ],
        };

        var rendered = AllowlistRenderer.Render(state);

        Assert.Collection(rendered.Modules,
            first =>
            {
                Assert.Equal("payments-1", first.Id);
                Assert.Equal("11111111-1111-1111-1111-111111111111", first.Appid);
                Assert.Null(first.Subnet);
                Assert.Equal(["api.stripe.com"], first.AllowedHosts);
                Assert.Equal("enforce", first.Action);
            },
            second =>
            {
                Assert.Equal("payments-2", second.Id);
                Assert.Null(second.Appid);
                Assert.Equal("10.2.0.0/23", second.Subnet);
                Assert.Equal(["api.stripe.com"], second.AllowedHosts);
            });
    }

    /// <summary>Secure by default, matching the proxy's own normalizeAction.</summary>
    [Theory]
    [InlineData("report", "report")]
    [InlineData("open", "open")]
    [InlineData("enforce", "enforce")]
    [InlineData(" Report ", "report")]
    [InlineData("reprot", "enforce")]
    [InlineData("", "enforce")]
    [InlineData(null, "enforce")]
    public void An_unrecognised_action_renders_as_enforce(string? action, string expected)
    {
        var state = new StateDocument
        {
            Rulesets =
            [
                new Ruleset
                {
                    Name = "r",
                    Subjects = [new Subject { Appid = "11111111-1111-1111-1111-111111111111" }],
                    Content = new RulesetContent { AllowedHosts = [], Action = action },
                },
            ],
        };

        Assert.Equal(expected, AllowlistRenderer.Render(state).Modules.Single().Action);
    }

    /// <summary>The render is a pure projection: ruleset order in the store must not change it,
    /// or the blob's ETag would flap and the proxy would reload for nothing.</summary>
    [Fact]
    public void Rendering_is_stable_regardless_of_store_order()
    {
        Ruleset Make(string name) => new()
        {
            Name = name,
            Subjects = [new Subject { Netid = "10.0.0.0/24" }],
            Content = new RulesetContent { AllowedHosts = ["a.example.com"] },
        };

        var ascending = new StateDocument { Rulesets = [Make("alpha"), Make("beta")] };
        var descending = new StateDocument { Rulesets = [Make("beta"), Make("alpha")] };

        Assert.Equal(
            StateJson.Serialize(AllowlistRenderer.Render(ascending)),
            StateJson.Serialize(AllowlistRenderer.Render(descending)));
    }

    /// <summary>The platform baseline passes through untouched — it is not a ruleset and the
    /// control plane never rewrites it.</summary>
    [Fact]
    public void Fallback_passes_through()
    {
        var state = new StateDocument { Fallback = new Fallback { AllowedHosts = ["baseline.example.com"] } };

        Assert.Equal(["baseline.example.com"], AllowlistRenderer.Render(state).Fallback!.AllowedHosts);
    }
}
