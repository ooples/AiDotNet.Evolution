using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed partial class AdaptiveIslandSearchTests
{
    public enum AllocationStateFault
    {
        EpochCapacity, CompletedEpochFloor, ProposalTotal, GenerationTotal, DecisionCount,
        ZeroTotalGeneration, LastDecisionGeneration, NullLastDecision, NullDecision,
        NonPositiveDecisionGeneration, UnorderedDecisionGeneration, DecisionBeyondLastGeneration,
        NegativeDecisionIsland, OutOfRangeDecisionIsland, DisabledRestartDecision,
        PendingIslandAttribution, PendingRestartAttribution
    }

    public enum RequiredPolicyField { VersionHash, Islands, Pending, Decisions, Children }
    public enum MissingFieldRepresentation { Omitted, ExplicitNull }
    public enum ChildCompatibilityFault { MissingFirstState, MissingLaterState, UnexpectedLastState }
    private enum RestoreBehavior { Accept, Reject }

    public static TheoryData<AllocationStateFault> AllocationStateFaults
    {
        get
        {
            var data = new TheoryData<AllocationStateFault>();
            foreach (AllocationStateFault fault in Enum.GetValues(typeof(AllocationStateFault))) data.Add(fault);
            return data;
        }
    }

    public static TheoryData<RequiredPolicyField, MissingFieldRepresentation> MissingPolicyFields
    {
        get
        {
            var data = new TheoryData<RequiredPolicyField, MissingFieldRepresentation>();
            foreach (RequiredPolicyField policyField in Enum.GetValues(typeof(RequiredPolicyField)))
                foreach (MissingFieldRepresentation representation in Enum.GetValues(typeof(MissingFieldRepresentation)))
                    data.Add(policyField, representation);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllocationStateFaults))]
    public async Task OrderedStateValidationRejectsCorruptionBeforeAnyChildRestore(AllocationStateFault fault)
    {
        var source = CreateRestorePolicy(enableRestarts: fault != AllocationStateFault.DisabledRestartDecision);
        int generations = fault switch
        {
            AllocationStateFault.ZeroTotalGeneration => 0,
            AllocationStateFault.EpochCapacity => 13,
            AllocationStateFault.CompletedEpochFloor => 12,
            _ => 11
        };
        await PopulateRestorePolicy(source.Policy, generations);
        var json = ParsePolicyState(source.Policy.CaptureState());
        CorruptAllocationState(json, fault);
        var target = CreateRestorePolicy(enableRestarts: fault != AllocationStateFault.DisabledRestartDecision);
        string before = target.Policy.CaptureState();

        var error = Assert.Throws<InvalidDataException>(() => target.Policy.RestoreState(json.ToJsonString()));

        Assert.Equal(ExpectedStateError(fault), error.Message);
        Assert.Equal(0, target.First.Restores);
        Assert.Equal(0, target.Second.Restores);
        Assert.Equal(before, target.Policy.CaptureState());
    }

    [Theory]
    [MemberData(nameof(MissingPolicyFields))]
    public void RequiredStateFieldsAreNotReplacedWithEmptyDefaults(
        RequiredPolicyField field, MissingFieldRepresentation representation)
    {
        var target = CreateRestorePolicy();
        string before = target.Policy.CaptureState();
        var json = ParsePolicyState(before);
        string property = field switch
        {
            RequiredPolicyField.VersionHash => "VersionHash",
            RequiredPolicyField.Islands => "Islands",
            RequiredPolicyField.Pending => "Pending",
            RequiredPolicyField.Decisions => "Decisions",
            RequiredPolicyField.Children => "Children",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        switch (representation)
        {
            case MissingFieldRepresentation.Omitted: Assert.True(json.Remove(property)); break;
            case MissingFieldRepresentation.ExplicitNull: json[property] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(representation));
        }

        var error = Assert.Throws<InvalidDataException>(() => target.Policy.RestoreState(json.ToJsonString()));

        Assert.Equal("The island state is incompatible or incomplete.", error.Message);
        Assert.Equal(0, target.First.Restores);
        Assert.Equal(0, target.Second.Restores);
        Assert.Equal(before, target.Policy.CaptureState());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(300)]
    public async Task ValidAllocationAndHistoryBoundariesRoundTripWithoutEarlyPublication(int generations)
    {
        var source = CreateRestorePolicy();
        await PopulateRestorePolicy(source.Policy, generations);
        string checkpoint = source.Policy.CaptureState();
        var target = CreateRestorePolicy();
        var restoreOrder = new List<int>();
        target.First.OnRestore = () =>
        {
            Assert.All(target.Policy.Statistics, island => Assert.Equal(0, island.Proposals));
            Assert.Empty(target.Policy.RecentDecisions);
            restoreOrder.Add(0);
        };
        target.Second.OnRestore = () =>
        {
            Assert.All(target.Policy.Statistics, island => Assert.Equal(0, island.Proposals));
            Assert.Empty(target.Policy.RecentDecisions);
            restoreOrder.Add(1);
        };

        target.Policy.RestoreState(checkpoint);

        Assert.Equal(new[] { 0, 1 }, restoreOrder);
        Assert.Equal(checkpoint, target.Policy.CaptureState());
        Assert.Equal(generations, target.Policy.Statistics.Sum(island => island.Proposals));
        Assert.Equal(Math.Min(256, generations), target.Policy.RecentDecisions.Count);
    }

    [Fact]
    public async Task LastGenerationMayBeLongMaxValueWithoutConsecutiveGenerationAssumptions()
    {
        var source = CreateRestorePolicy();
        await PopulateRestorePolicy(source.Policy, 1);
        var json = ParsePolicyState(source.Policy.CaptureState());
        json["LastGeneration"] = long.MaxValue;
        StateObject(StateArray(json, "Decisions")[0])["Generation"] = long.MaxValue;
        var pending = StateObject(json["Pending"]);
        var attribution = StateObject(pending["1"]);
        Assert.True(pending.Remove("1"));
        pending[long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)] = attribution;
        var target = CreateRestorePolicy();

        target.Policy.RestoreState(json.ToJsonString());

        Assert.Equal(long.MaxValue, Assert.Single(target.Policy.RecentDecisions).Generation);
        Assert.Equal(1, target.First.Restores);
        Assert.Equal(1, target.Second.Restores);
        Assert.Equal(json.ToJsonString(), target.Policy.CaptureState());
    }

    [Fact]
    public void MissingScalarCountersRetainTheirExistingZeroDefaultForEmptyState()
    {
        var target = CreateRestorePolicy();
        string before = target.Policy.CaptureState();
        var json = ParsePolicyState(before);
        Assert.True(json.Remove("Epoch"));
        Assert.True(json.Remove("LastGeneration"));

        target.Policy.RestoreState(json.ToJsonString());

        Assert.Equal(before, target.Policy.CaptureState());
        Assert.Equal(1, target.First.Restores);
        Assert.Equal(1, target.Second.Restores);
    }

    [Theory]
    [InlineData(ChildCompatibilityFault.MissingFirstState)]
    [InlineData(ChildCompatibilityFault.MissingLaterState)]
    [InlineData(ChildCompatibilityFault.UnexpectedLastState)]
    public async Task EveryChildCompatibilityCheckFinishesBeforeTheFirstRestore(ChildCompatibilityFault fault)
    {
        var source = CreateRestorePolicy();
        await PopulateRestorePolicy(source.Policy, 11);
        var json = ParsePolicyState(source.Policy.CaptureState());
        var children = StateArray(json, "Children");
        switch (fault)
        {
            case ChildCompatibilityFault.MissingFirstState: children[0] = null; break;
            case ChildCompatibilityFault.MissingLaterState: children[2] = null; break;
            case ChildCompatibilityFault.UnexpectedLastState: children[3] = "unexpected"; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        var target = CreateRestorePolicy();
        string before = target.Policy.CaptureState();

        var error = Assert.Throws<InvalidDataException>(() => target.Policy.RestoreState(json.ToJsonString()));

        Assert.Equal("The island child state is missing or unexpected.", error.Message);
        Assert.Equal(0, target.First.Restores);
        Assert.Equal(0, target.Second.Restores);
        Assert.Equal(before, target.Policy.CaptureState());
    }

    [Fact]
    public void EmptyCheckpointableChildPayloadIsPresentAndLeftToTheChildContract()
    {
        var target = CreateRestorePolicy();
        var json = ParsePolicyState(target.Policy.CaptureState());
        StateArray(json, "Children")[0] = "";

        target.Policy.RestoreState(json.ToJsonString());

        Assert.Equal("", target.First.State);
        Assert.Equal(1, target.First.Restores);
        Assert.Equal(1, target.Second.Restores);
    }

    [Fact]
    public async Task ARejectingChildDoesNotPublishPolicyStateAndDoesNotPromiseChildRollback()
    {
        var source = CreateRestorePolicy();
        await PopulateRestorePolicy(source.Policy, 11);
        var target = CreateRestorePolicy(secondBehavior: RestoreBehavior.Reject);

        var error = Assert.Throws<InvalidDataException>(() => target.Policy.RestoreState(source.Policy.CaptureState()));

        Assert.Equal("The test child rejected its state.", error.Message);
        Assert.Equal(1, target.First.Restores);
        Assert.Equal(source.First.State, target.First.State);
        Assert.Equal(1, target.Second.Restores);
        Assert.All(target.Policy.Statistics, island => Assert.Equal(0, island.Proposals));
        Assert.Empty(target.Policy.RecentDecisions);
        var unpublished = ParsePolicyState(target.Policy.CaptureState());
        Assert.Empty(StateObject(unpublished["Pending"]));
        Assert.Equal(0, Assert.IsAssignableFrom<JsonValue>(unpublished["Epoch"]).GetValue<long>());
        Assert.Equal(0, Assert.IsAssignableFrom<JsonValue>(unpublished["LastGeneration"]).GetValue<long>());
        // The documented contract requires discarding this instance after a child failure.
    }

    [Fact]
    public async Task RestoredPolicyCollectionsAndChildStateAreIsolatedFromTheSource()
    {
        var source = CreateRestorePolicy();
        await PopulateRestorePolicy(source.Policy, 11);
        string checkpoint = source.Policy.CaptureState();
        var target = CreateRestorePolicy();
        target.Policy.RestoreState(checkpoint);

        source.Policy.Observe(Evaluation(11, 0, 1), EvolutionArchiveInsertionResult.Inserted);
        await source.Policy.ProposeAsync(Context(12, 0));
        string advancedSource = source.Policy.CaptureState();
        Assert.NotEqual(checkpoint, advancedSource);
        Assert.Equal(checkpoint, target.Policy.CaptureState());

        target.Policy.Observe(Evaluation(11, 0, 1), EvolutionArchiveInsertionResult.Inserted);
        Assert.NotEqual(checkpoint, target.Policy.CaptureState());
        Assert.Equal(advancedSource, source.Policy.CaptureState());
    }

    private static void CorruptAllocationState(JsonObject json, AllocationStateFault fault)
    {
        var islands = StateArray(json, "Islands");
        var first = StateObject(islands[0]);
        var second = StateObject(islands[1]);
        var decisions = StateArray(json, "Decisions");
        switch (fault)
        {
            case AllocationStateFault.EpochCapacity:
                json["Epoch"] = 2;
                first["EpochProposals"] = 4;
                second["EpochProposals"] = 1;
                break;
            case AllocationStateFault.CompletedEpochFloor: first["EpochProposals"] = 4; second["EpochProposals"] = 0; break;
            case AllocationStateFault.ProposalTotal: first["EpochProposals"] = 1; break;
            case AllocationStateFault.GenerationTotal:
                json["LastGeneration"] = 10;
                StateObject(json["Pending"]).Clear();
                first["Outcomes"] = 8;
                first["Fresh"] = 8;
                StateArray(first, "Recent").Add(new JsonObject { ["Gain"] = 1, ["Diversity"] = 1 });
                break;
            case AllocationStateFault.DecisionCount: decisions.RemoveAt(0); break;
            case AllocationStateFault.ZeroTotalGeneration: json["LastGeneration"] = 1; break;
            case AllocationStateFault.LastDecisionGeneration: json["LastGeneration"] = 12; break;
            case AllocationStateFault.NullLastDecision: decisions[decisions.Count - 1] = null; break;
            case AllocationStateFault.NullDecision: decisions[0] = null; break;
            case AllocationStateFault.NonPositiveDecisionGeneration: StateObject(decisions[0])["Generation"] = 0; break;
            case AllocationStateFault.UnorderedDecisionGeneration: StateObject(decisions[1])["Generation"] = 1; break;
            case AllocationStateFault.DecisionBeyondLastGeneration: StateObject(decisions[0])["Generation"] = 12; break;
            case AllocationStateFault.NegativeDecisionIsland: StateObject(decisions[0])["Island"] = -1; break;
            case AllocationStateFault.OutOfRangeDecisionIsland: StateObject(decisions[0])["Island"] = 2; break;
            case AllocationStateFault.DisabledRestartDecision: StateObject(decisions[0])["Restart"] = true; break;
            case AllocationStateFault.PendingIslandAttribution: StateObject(decisions[decisions.Count - 1])["Island"] = 1; break;
            case AllocationStateFault.PendingRestartAttribution: StateObject(decisions[decisions.Count - 1])["Restart"] = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
    }

    private static string ExpectedStateError(AllocationStateFault fault) => fault switch
    {
        AllocationStateFault.EpochCapacity or AllocationStateFault.CompletedEpochFloor or AllocationStateFault.ProposalTotal or
        AllocationStateFault.GenerationTotal or AllocationStateFault.DecisionCount or AllocationStateFault.ZeroTotalGeneration or
        AllocationStateFault.LastDecisionGeneration or AllocationStateFault.NullLastDecision => "The island allocation history is inconsistent.",
        _ => "The island decision history is invalid."
    };

    private static JsonObject ParsePolicyState(string state) => StateObject(JsonNode.Parse(state));
    private static JsonObject StateObject(JsonNode? node) => Assert.IsType<JsonObject>(node);
    private static JsonArray StateArray(JsonObject state, string property) => Assert.IsType<JsonArray>(state[property]);

    private static async Task PopulateRestorePolicy(AdaptiveIslandSearch<TestGenome> policy, int generations)
    {
        for (int generation = 1; generation <= generations; generation++)
        {
            int island = generation % 4 == 2 ? 1 : 0;
            await policy.ProposeAsync(Context(generation, island));
            if (generation != generations)
                policy.Observe(Evaluation(generation, island, 1), EvolutionArchiveInsertionResult.Inserted);
        }
    }

    private static (AdaptiveIslandSearch<TestGenome> Policy, RestoreTrackingChild First, RestoreTrackingChild Second)
        CreateRestorePolicy(bool enableRestarts = true, RestoreBehavior secondBehavior = RestoreBehavior.Accept)
    {
        var first = new RestoreTrackingChild();
        var second = new RestoreTrackingChild(secondBehavior);
        var policy = new AdaptiveIslandSearch<TestGenome>(new[]
        {
            new EvolutionIslandStrategy<TestGenome>("first", first, new IncrementVariation()),
            new EvolutionIslandStrategy<TestGenome>("second", second, new IncrementVariation())
        }, new EvolutionIslandPolicyOptions(proposalsPerIslandPerEpoch: 2, enableRestarts: enableRestarts));
        return (policy, first, second);
    }

    private sealed class RestoreTrackingChild(RestoreBehavior behavior = RestoreBehavior.Accept) : ICheckpointableVariationOperator<TestGenome>
    {
        public string Id => "restore-tracking";
        public string VersionHash => "v1";
        public string State { get; private set; } = "initial";
        public int Restores { get; private set; }
        public Action? OnRestore { get; set; }
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default)
        {
            State = context.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new(new TestGenome(1));
        }
        public string CaptureState() => State;
        public void RestoreState(string state)
        {
            Restores++;
            OnRestore?.Invoke();
            if (behavior == RestoreBehavior.Reject) throw new InvalidDataException("The test child rejected its state.");
            State = state;
        }
    }
}
