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

    private static EgressPool Pool(string? prefix, int prefixLength, int capacity, int inUse) =>
        new("egress-prefix", prefix, prefixLength, capacity, inUse, [], Freshness.Now);

    private static ProxyInstance Instance(string? health = null, string? power = "running") =>
        new("0", "proxy-vmss_0", "Succeeded", power, health, null, null);
}
