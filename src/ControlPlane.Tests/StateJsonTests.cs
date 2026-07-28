using System.Text.Json;
using ControlPlane.Model;

namespace ControlPlane.Tests;

public class StateJsonTests
{
    /// <summary>
    /// The state the API writes has to stay valid against allowlist/rulesets.schema.json, which
    /// forbids extra properties. A derived member that serializes would silently invalidate every
    /// blob the control plane writes.
    /// </summary>
    [Fact]
    public void A_subject_serializes_only_its_schema_fields()
    {
        var json = StateJson.Serialize(new Subject { Appid = "11111111-1111-1111-1111-111111111111" });

        var properties = JsonDocument.Parse(json).RootElement
            .EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(["appid"], properties);
    }

    /// <summary>Round-tripping must be lossless: the store is read, spliced, and written back on
    /// every single request, so anything dropped here is dropped permanently.</summary>
    [Fact]
    public void State_round_trips_without_loss()
    {
        var original = Repo.ReadText("allowlist/rulesets.json");

        var round = StateJson.Serialize(StateJson.Deserialize<StateDocument>(original));

        Assert.Equal(original.TrimEnd(), round.TrimEnd());
    }
}
