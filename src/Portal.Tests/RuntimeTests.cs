using Portal.Clients;
using Portal.Pages;

namespace Portal.Tests;

/// <summary>
/// The egress-address grid, which is the one piece of arithmetic on the Runtime surface whose
/// being wrong would mislead an operator about something outside this deployment: the prefix is
/// the set of addresses partners allowlist, so "how many are left" is how many nodes the fleet can
/// still add before one egresses from an address nobody has allowlisted.
/// </summary>
public class RuntimeTests
{
    [Fact]
    public void Slots_are_labelled_from_the_prefix_and_named_by_the_node_holding_them()
    {
        var pool = Pool("20.13.4.8/29", prefixLength: 29, capacity: 8, inUse: 2);

        var slots = RuntimeModel.Slots(pool, new Dictionary<string, string>
        {
            ["20.13.4.9"] = "proxy-vmss_1",
        });

        Assert.Equal(8, slots.Count);
        Assert.Equal(".8", slots[0].Label);
        Assert.Equal("20.13.4.9", slots[1].Address);
        Assert.Equal("proxy-vmss_1", slots[1].Node);
        Assert.True(slots[1].InUse);
    }

    /// <summary>
    /// The prefix knows how many addresses are assigned; the scale set knows which node holds
    /// which. When they disagree the prefix wins, because a panel that only marked what it could
    /// attribute would under-report how full the pool is — the one error this panel must not make.
    /// </summary>
    [Fact]
    public void An_assigned_address_no_node_claims_is_still_drawn_as_in_use()
    {
        var pool = Pool("20.13.4.8/29", prefixLength: 29, capacity: 8, inUse: 3);

        var slots = RuntimeModel.Slots(pool, new Dictionary<string, string>
        {
            ["20.13.4.9"] = "proxy-vmss_1",
        });

        Assert.Equal(3, slots.Count(slot => slot.InUse));
        Assert.Single(slots, slot => slot is { InUse: true, Node: not null });
    }

    [Fact]
    public void An_exhausted_prefix_leaves_no_spare_slot()
    {
        var pool = Pool("20.13.4.8/30", prefixLength: 30, capacity: 4, inUse: 4);

        var slots = RuntimeModel.Slots(pool, new Dictionary<string, string>());

        Assert.Equal(0, pool.Available);
        Assert.All(slots, slot => Assert.True(slot.InUse));
    }

    /// <summary>A prefix too large to draw still gets its counts; only the grid is dropped.</summary>
    [Fact]
    public void A_prefix_too_large_to_draw_produces_no_grid()
    {
        var pool = Pool("20.13.0.0/24", prefixLength: 24, capacity: 256, inUse: 3);

        Assert.Empty(RuntimeModel.Slots(pool, new Dictionary<string, string>()));
    }

    /// <summary>Everything else on the surface degrades to a message; the grid degrades to
    /// index labels, because the count is still worth showing when the CIDR is not readable.</summary>
    [Fact]
    public void A_prefix_that_cannot_be_parsed_still_counts()
    {
        var pool = Pool(prefix: null, prefixLength: 29, capacity: 8, inUse: 2);

        var slots = RuntimeModel.Slots(pool, new Dictionary<string, string>());

        Assert.Equal(8, slots.Count);
        Assert.All(slots, slot => Assert.Null(slot.Address));
        Assert.Equal(2, slots.Count(slot => slot.InUse));
    }

    /// <summary>
    /// The absent health extension is a third state. A node reporting no health at all must not
    /// read as a node reporting itself healthy.
    /// </summary>
    [Theory]
    [InlineData("HealthState/healthy", "Healthy", "enforce")]
    [InlineData("HealthState/unhealthy", "Unhealthy", "open")]
    [InlineData(null, "No health probe", "plain")]
    public void Health_never_reads_as_healthy_without_a_verdict(string? code, string text, string variant)
    {
        var pill = RuntimeModel.Health(Instance(health: code));

        Assert.Equal(text, pill.Text);
        Assert.Equal(variant, pill.Variant);
    }

    /// <summary>A node that is not powered on does not wear the healthy colour.</summary>
    [Fact]
    public void A_stopped_node_is_not_coloured_as_running()
    {
        Assert.NotEqual(
            RuntimeModel.PowerColour(Instance(power: "running")),
            RuntimeModel.PowerColour(Instance(power: "deallocated")));
    }

    // ---- the schematic: a stage the console could not read -------------------------------------

    /// <summary>
    /// The stage-level counterpart to <see cref="Health_never_reads_as_healthy_without_a_verdict"/>,
    /// and the invariant this whole surface most risks breaking: a picture wants to be complete.
    /// A stage whose source did not answer is unlit and hatched — never the appearance a healthy
    /// stage wears, and never the appearance an unhealthy one wears either.
    /// </summary>
    [Fact]
    public void An_unread_stage_never_wears_the_appearance_of_a_healthy_one()
    {
        var blind = RuntimeModel.Derive();

        foreach (var stage in Stages(blind))
        {
            Assert.Equal(LampState.Unread, stage.Lamp);
            Assert.True(stage.Unread);
            Assert.NotEqual(RuntimeModel.LampClass(LampState.Ok), stage.LampClass);
            Assert.NotEqual(RuntimeModel.LampClass(LampState.Warn), stage.LampClass);
            Assert.NotEqual(RuntimeModel.LampClass(LampState.Bad), stage.LampClass);
        }
    }

    /// <summary>
    /// The other half of the same refusal. "Not readable" and "reporting zero" are opposite
    /// findings, and a schematic that drew an unread stage as an empty one would be inventing an
    /// outage.
    /// </summary>
    [Fact]
    public void An_unread_stage_is_not_reported_as_reporting_zero()
    {
        var blind = RuntimeModel.Derive();

        foreach (var stage in Stages(blind))
        {
            Assert.NotEqual("0", stage.Value);
            Assert.Equal("Not readable", stage.Sub);
        }
    }

    // ---- the schematic: ducts ------------------------------------------------------------------

    /// <summary>
    /// A full prefix caps growth; it does not stop today's requests. Drawing that duct as stopped
    /// would say traffic has ceased, which is false and is exactly the kind of over-claim a diagram
    /// makes easily (design.md D5).
    /// </summary>
    [Fact]
    public void A_full_prefix_constrains_the_duct_without_stopping_it()
    {
        var duct = RuntimeModel.Derive(pool: Pool("20.13.4.8/31", 31, capacity: 2, inUse: 2)).EgressDuct;

        Assert.Equal(DuctTone.Constrained, duct.Tone);
        Assert.NotEqual(DuctTone.Stopped, duct.Tone);
        Assert.Equal("FULL", duct.Chip);
    }

    /// <summary>An unread prefix does not get a tone that claims anything about the traffic.</summary>
    [Fact]
    public void An_unread_prefix_leaves_its_ducts_unknown()
    {
        var model = RuntimeModel.Derive();

        Assert.Equal(DuctTone.Unknown, model.EgressDuct.Tone);
        Assert.Equal(DuctTone.Unknown, model.OutletDuct.Tone);
    }

    // ---- the schematic: the consequence --------------------------------------------------------

    /// <summary>
    /// The sentence the surface exists for. Every address assigned means the prefix — not the
    /// scale set — is the fleet's ceiling, and a further node would leave from an address no
    /// partner has allowlisted.
    /// </summary>
    [Fact]
    public void An_exhausted_prefix_reads_as_the_fleet_being_constrained()
    {
        var consequence = RuntimeModel.Derive(
            scaleSet: Fleet(capacity: 2, running: 2, Healthy("0"), Healthy("1")),
            pool: Pool("20.13.4.8/31", 31, capacity: 2, inUse: 2),
            loadBalancer: HealthyLoadBalancer()).Consequence;

        Assert.Equal("bad", consequence.Tone);
        Assert.Contains("ceiling", consequence.Headline, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The reading the separate panels made an operator assemble themselves: trouble at one stage,
    /// headroom at the next. A degraded fleet with spare addresses can replace the node from inside
    /// the block partners already allowlist.
    /// </summary>
    [Fact]
    public void A_degraded_fleet_with_spare_addresses_reads_as_recoverable()
    {
        var consequence = RuntimeModel.Derive(
            scaleSet: Fleet(capacity: 3, running: 2, Healthy("0"), Healthy("1")),
            pool: Pool("20.13.4.8/29", 29, capacity: 8, inUse: 6),
            loadBalancer: HealthyLoadBalancer()).Consequence;

        Assert.Equal("ok", consequence.Tone);
    }

    /// <summary>
    /// <b>The quiet day still renders.</b> A bar that disappears when nothing is wrong trains an
    /// operator not to look at it — and this bar is the accessible carrier of the schematic's whole
    /// argument, because the ducts are CSS and invisible to assistive technology (design.md D3).
    /// </summary>
    [Fact]
    public void An_unremarkable_path_still_states_how_far_the_fleet_can_grow()
    {
        var consequence = RuntimeModel.Derive(
            scaleSet: Fleet(capacity: 2, running: 2, Healthy("0"), Healthy("1")),
            pool: Pool("20.13.4.8/29", 29, capacity: 8, inUse: 2),
            loadBalancer: HealthyLoadBalancer()).Consequence;

        Assert.Equal(string.Empty, consequence.Tone);
        Assert.False(string.IsNullOrWhiteSpace(consequence.Headline));
        Assert.Contains("6", consequence.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// One unread source and the bar states neither that the fleet is constrained nor that it is
    /// not. The console does not guess on Resource Manager's behalf.
    /// </summary>
    [Fact]
    public void An_unread_source_leaves_the_fleet_neither_constrained_nor_unconstrained()
    {
        var consequence = RuntimeModel.Derive(
            scaleSet: Fleet(capacity: 2, running: 2, Healthy("0"), Healthy("1")),
            pool: null,
            loadBalancer: HealthyLoadBalancer()).Consequence;

        Assert.Equal("dim", consequence.Tone);
        Assert.Contains("unread", consequence.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ceiling", consequence.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("can grow", consequence.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<StageView> Stages(RuntimeModel model) =>
        [model.LoadBalancerStage, model.FleetStage, model.PrefixStage];

    private static EgressPool Pool(string? prefix, int prefixLength, int capacity, int inUse) =>
        new("egress-prefix", prefix, prefixLength, capacity, inUse, [], Freshness.Now);

    private static ProxyInstance Instance(string? health = null, string? power = "running") =>
        new("0", "proxy-vmss_0", "Succeeded", power, health, null, null);

    private static ProxyInstance Healthy(string id) =>
        new(id, $"proxy-vmss_{id}", "Succeeded", "running", "HealthState/healthy", null, "azure-linux-3-arm64");

    private static ScaleSetStatus Fleet(int capacity, int running, params ProxyInstance[] instances) =>
        new("egproxy-vmss", capacity, running, instances, Freshness.Now);

    /// <summary>Both metrics reporting 100% on every sample — the load balancer readable and fine,
    /// so a test about the prefix is not quietly a test about an unread stage.</summary>
    private static IReadOnlyList<LoadBalancerSignal> HealthyLoadBalancer() =>
    [
        RuntimeModel.Signal("Data path", Series(100, 100, 100),
            "Available", "Degraded", "Unavailable"),
        RuntimeModel.Signal("Health probes", Series(100, 100, 100),
            "All probes passing", "Some probes failing", "All probes failing"),
    ];

    private static MetricSeries Series(params double[] values) =>
        new("VipAvailability", "%", TimeSpan.FromMinutes(1),
            [.. values.Select((value, index) =>
                new MetricPoint(DateTimeOffset.UtcNow.AddMinutes(index - values.Length), value))],
            Freshness.Now);
}
