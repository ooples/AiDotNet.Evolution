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
