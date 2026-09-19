using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed partial class AdaptiveIslandSearchTests
{
    [Theory]
    [InlineData("root")]
    [InlineData("island")]
    [InlineData("pending")]
    [InlineData("decision")]
    [InlineData("reward")]
    public async Task EveryCheckpointFieldIsRequiredEvenWhenItsDefaultLooksValid(string section)
    {
        var child = new RecordingChild(); var policy = Single(child);
        await policy.ProposeAsync(Context(1, 0)); policy.Observe(Evaluation(1, 0, 0), null);
        await policy.ProposeAsync(Context(2, 0));
        string before = policy.CaptureState();
        JsonObject Section(JsonNode root) => (JsonObject)(section switch
        {
            "root" => root,
            "island" => root["Islands"]![0]!,
            "pending" => root["Pending"]!["2"]!,
            "decision" => root["Decisions"]![0]!,
            _ => root["Islands"]![0]!["Recent"]![0]!
        });
        foreach (string field in Section(JsonNode.Parse(before)!).Select(pair => pair.Key).ToArray())
        {
            var json = JsonNode.Parse(before)!;
            Section(json).Remove(field);
            Assert.Throws<InvalidDataException>(() => policy.RestoreState(json.ToJsonString()));
            Assert.Equal(before, policy.CaptureState()); Assert.Equal(0, child.Restores);
        }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public void DuplicateOrUnknownDefaultFieldsAreRejected(string corruption)
    {
        var policy = Policy(); string before = policy.CaptureState();
        string invalid = before.Insert(1, corruption == "duplicate" ? "\"Epoch\":0," : "\"Unexpected\":0,");
        Assert.Throws<InvalidDataException>(() => policy.RestoreState(invalid));
        Assert.Equal(before, policy.CaptureState());
    }
}
