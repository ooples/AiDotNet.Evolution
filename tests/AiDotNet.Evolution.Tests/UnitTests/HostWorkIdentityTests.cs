#if !NET471
using System.Text.Json;
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class HostWorkIdentityTests
{
    [Fact]
    public async Task StrictHostFencesCrossSessionMissingMismatchedAndDuplicateTickets()
    {
        using var first = HostSession.Open(Config());
        using var second = HostSession.Open(Config());
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var a = Assert.Single(await first.AskAsync(1, guard.Token));
        var b = Assert.Single(await second.AskAsync(1, guard.Token));
        Assert.True(first.RequiresWorkIdentity);
        Assert.Equal(first.CompatibilityHash, second.CompatibilityHash);
        Assert.NotEqual(a.WorkIdentity!.LeaseId, b.WorkIdentity!.LeaseId);
        Assert.Equal(0, first.Tell(new[] { Score(a, false) }));
        Assert.Equal(0, first.Tell(new[] { Score(b) }));
        var mismatched = Score(a); mismatched.EvaluationId++;
        Assert.Equal(0, first.Tell(new[] { mismatched }));
        // Exercise the actual AOT context in both directions, not reflection-only test serialization.
        string frame = JsonSerializer.Serialize(new Request { Id = 42, Op = "tell", Results = new() { Score(a) } }, HostJsonContext.Default.Request);
        Request request = Assert.IsType<Request>(Protocol.ParseRequest(frame, out var error, out long id));
        Assert.Null(error); Assert.Equal(42, id);
        Assert.Equal(1, first.Tell(request.Results!)); Assert.Equal(0, first.Tell(request.Results!));
        Assert.Equal(1, second.Tell(new[] { Score(b) }));
        Assert.Empty(await first.AskAsync(1, guard.Token));
        Assert.Equal(1, (await first.FinishAsync()).Best!.Quality);
    }

    [Fact]
    public async Task MalformedLaterTicketCannotCommitAnUnacknowledgedPrefix()
    {
        using var session = HostSession.Open(Config());
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Candidate a = Assert.Single(await session.AskAsync(1, guard.Token));
        var invalid = Score(a); invalid.WorkIdentity = new WorkIdentityDto();
        Assert.Throws<ArgumentException>(() => session.Tell(new[] { Score(a), invalid }));
        Assert.Equal(1, session.Tell(new[] { Score(a) }));
    }

    [Fact]
    public async Task HostRetryRequiresReplacementTicket()
    {
        var config = Config(); config.MaxEvaluations = 2; config.MaxRetries = 1; config.EvaluationTimeoutMs = 1000;
        using var session = HostSession.Open(config);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Candidate first = Assert.Single(await session.AskAsync(1, guard.Token));
        Candidate retry = Assert.Single(await session.AskAsync(1, guard.Token));
        Assert.Equal(first.EvaluationId, retry.EvaluationId); Assert.Equal(2, retry.WorkIdentity!.Attempt);
        Assert.Equal(0, session.Tell(new[] { Score(first), Score(retry, false) }));
        Assert.Equal(1, session.Tell(new[] { Score(retry) }));
        Assert.Empty(await session.AskAsync(1, guard.Token));
        Assert.Equal(1, (await session.FinishAsync()).Best!.Quality);
    }

    [Fact]
    public void TaskEvaluatorAndOrderedParameterSchemaAllGuardCompatibility()
    {
        var hashes = new HashSet<string>();
        for (int i = 0; i < 8; i++)
        {
            var config = Config(); config.MaxEvaluations = 0;
            switch (i)
            {
                case 1: config.TaskIdentity!.TaskId = "other"; break;
                case 2: config.TaskIdentity!.TaskVersionHash = "v2"; break;
                case 3: config.TaskIdentity!.EvaluatorVersionHash = "v2"; break;
                case 4: config.Parameters[0].Name = "other"; break;
                case 5: config.Parameters[0].Step = .25; break;
                case 6: config.Parameters[0].Integral = true; break;
                case 7: config.Parameters[0].Min = -2; break;
            }
            using var session = HostSession.Open(config); Assert.True(hashes.Add(session.CompatibilityHash));
        }
    }

    [Fact]
    public void HostRejectsInvalidIdentityAndUnfencedRetriesBeforeStarting()
    {
        var config = Config(); config.TaskIdentity!.TaskId = " ";
        Assert.Throws<ArgumentException>(() => HostSession.Open(config));
        config = Config(); config.TaskIdentity = null; config.MaxRetries = 1;
        Assert.Throws<ArgumentException>(() => HostSession.Open(config));
        config = Config(); config.EvaluationTimeoutMs = 0;
        Assert.Throws<ArgumentException>(() => HostSession.Open(config));
        config = Config(); config.MaxRetries = -1;
        Assert.Throws<ArgumentException>(() => HostSession.Open(config));
    }

    [Fact]
    public void ParameterCodecPreservesExactValuesAndRejectsForeignOrUnboundedVectors()
    {
        var space = new ParameterSpace(new[] { new ParameterDefinition("x|=é", -1, 1, .003, false) });
        var codec = new ParameterGenomeCodec(space);
        foreach (double value in new[] { -1d, -.234567, 0, .678901, 1 })
        {
            ParameterGenome genome = space.Create(new[] { value });
            Assert.Equal(genome.CanonicalId(), codec.Deserialize(codec.Serialize(genome)).CanonicalId());
        }
        Assert.Throws<ArgumentException>(() => codec.Deserialize("[]"));
        Assert.Throws<ArgumentException>(() => codec.Deserialize("[2]"));
        Assert.Throws<ArgumentException>(() => codec.Deserialize(new string(' ', 100)));
        var other = new ParameterSpace(new[] { new ParameterDefinition("x|=é", -1, 1, .003, false) });
        Assert.Throws<ArgumentException>(() => codec.Serialize(other.Create(new[] { 0d })));
        Assert.Equal(codec.VersionHash, new ParameterGenomeCodec(other).VersionHash);
    }

    [Fact]
    public void ParameterCodecSchemaHashSeparatesAdjacentFieldsAndTracksEveryDeclaredAttribute()
    {
        string Hash(params ParameterDefinition[] definitions) =>
            new ParameterGenomeCodec(new ParameterSpace(definitions)).VersionHash;

        // The absolute value, computed independently of this code: SHA-256 over
        // "x120.50" + "y-1111".
        // The assertions below pin relationships between hashes, which a change to the encoding or
        // to how the bytes are fed to the hash would satisfy just as well; this pins the contract
        // itself, so persisted genomes cannot start being rejected without a deliberate edit here.
        Assert.Equal(
            "ordered-normalized-parameters-v2-canonical:67ed39341acb1c303b857ec430443b728451354903499402c72a61ff1e6ce4b6",
            Hash(new ParameterDefinition("x", 1, 2, .5, false), new ParameterDefinition("y", -1, 1, 1, true)));

        // Field separators are load-bearing, not decoration. These two spaces are genuinely different
        // -- one runs to 2 in steps of 11, the other to 21 in steps of 1 -- but their field values
        // concatenate to the same characters ("x" "1" "2" "11" "0" against "x" "1" "21" "1" "0").
        // Drop the separator and the hash declares them compatible, letting a genome built for one
        // space deserialize against the other.
        Assert.NotEqual(
            Hash(new ParameterDefinition("x", 1, 2, 11, false)),
            Hash(new ParameterDefinition("x", 1, 21, 1, false)));

        // Every declared attribute is part of the compatibility contract, and order is significant.
        var baseline = Hash(new ParameterDefinition("x", -1, 1, .5, false), new ParameterDefinition("y", 0, 2, .5, false));
        Assert.NotEqual(baseline, Hash(new ParameterDefinition("y", 0, 2, .5, false), new ParameterDefinition("x", -1, 1, .5, false)));
        Assert.NotEqual(baseline, Hash(new ParameterDefinition("x", -1, 1, .5, true), new ParameterDefinition("y", 0, 2, .5, false)));
        Assert.NotEqual(baseline, Hash(new ParameterDefinition("x", -1, 1, .25, false), new ParameterDefinition("y", 0, 2, .5, false)));
        Assert.NotEqual(baseline, Hash(new ParameterDefinition("x", -1, 1.5, .5, false), new ParameterDefinition("y", 0, 2, .5, false)));

        // Round-trip formatting, not the shortest representation: bounds that print the same under a
        // lossy format are distinct doubles and must stay distinguishable.
        Assert.NotEqual(
            Hash(new ParameterDefinition("x", 0, 0.1 + 0.2, 1, false)),
            Hash(new ParameterDefinition("x", 0, 0.3, 1, false)));
    }

    private static RunConfig Config() => new()
    {
        Parameters = new() { new ParameterConfig { Name = "x", Min = -1, Max = 1, Step = .5 } },
        Descriptors = new() { new DescriptorConfig { Name = "x", Min = -1, Max = 1, Bins = 4 } },
        TaskIdentity = new() { TaskId = "test", TaskVersionHash = "task-v1", EvaluatorVersionHash = "eval-v1" },
        MaxProposals = 1,
        MaxEvaluations = 1,
        MaxGenerations = 0,
    };

    private static TellResult Score(Candidate candidate, bool ticket = true) => new()
    {
        EvaluationId = candidate.EvaluationId,
        WorkIdentity = ticket ? candidate.WorkIdentity : null,
        Quality = 1,
        Descriptors = new() { ["x"] = 0 },
    };
}
#endif
