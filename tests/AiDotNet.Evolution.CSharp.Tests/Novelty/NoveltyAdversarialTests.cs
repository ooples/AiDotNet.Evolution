using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Programs.Novelty;
using AiDotNet.Evolution.CSharp.Tests.ModelRuntime;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Novelty;

public sealed class NoveltyAdversarialTests
{
    private static ProgramGenome A() => new("total = 0\nfor x in xs:\n    total += x\n");
    private static ProgramGenome B() => new(A().Source + "#\n");
    private static EvolutionEvaluationContext Context() => new(1, 99UL, 7UL, 1);

    [Fact]
    public void VectorAndBatchNeverExposeMutableBackingArrays()
    {
        var input = new[] { 1d, 0d }; var vector = new EmbeddingVector(input); input[0] = 0;
        Assert.Equal(1d, vector.Components[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<double>)vector.Components)[0] = 0);
        var batch = EmbeddingBatch.Success(new[] { vector });
        Assert.Throws<NotSupportedException>(() => ((IList<EmbeddingVector>)batch.Vectors)[0] = new(new[] { 0d, 1d }));
    }

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void CosineHandlesFiniteExtremeMagnitudes(double value)
    {
        var first = new EmbeddingVector(new[] { value, value });
        var opposite = new EmbeddingVector(new[] { -value, -value });
        Assert.Equal(1, EmbeddingVector.CosineSimilarity(first, first), 12);
        Assert.Equal(-1, EmbeddingVector.CosineSimilarity(first, opposite), 12);
    }

    [Fact]
    public void DegenerateVectorsAreNotEvidenceOfNovelty()
    {
        Assert.Throws<ArgumentException>(() => new EmbeddingVector(new[] { 0d, 0d }));
        Assert.Throws<ArgumentException>(() => new EmbeddingVector(new[] { double.NaN }));
        Assert.Throws<ArgumentException>(() => new EmbeddingVector(Enumerable.Repeat(1d, EmbeddingVector.MaximumDimensions + 1)));
        Assert.Throws<ArgumentException>(() => EmbeddingVector.CosineSimilarity(new(new[] { 1d }), new(new[] { 1d, 0d })));
        Assert.DoesNotContain("secret", EmbeddingBatch.Failure("secret").FailureReason);
    }

    [Theory]
    [InlineData("novelist")]
    [InlineData("novelty")]
    [InlineData("The code is NOVEL")]
    [InlineData("NOT_NOVEL_extra")]
    public void OnlyExplicitLeadingVerdictTokensAreAccepted(string text) =>
        Assert.Equal(ProgramNoveltyVerdict.Unavailable, LlmProgramNoveltyJudge.ParseVerdict(text));

    [Fact]
    public void OversizedVerdictsAreUnavailable() => Assert.Equal(ProgramNoveltyVerdict.Unavailable,
        LlmProgramNoveltyJudge.ParseVerdict("NOVEL " + new string('x', LlmProgramNoveltyJudge.MaxResponseChars)));

    [Fact]
    public async Task ZeroThresholdBypassesStructuralStageButNotOptionalJudge()
    {
        var judge = new ScriptedNoveltyJudge(ProgramNoveltyVerdict.NotNovel);
        var policy = new ProgramNoveltyPolicy(new(structuralNoveltyThreshold: 0), judge: judge);
        var decision = await policy.EvaluateAsync(new("completely different"), new[] { A() });
        Assert.False(decision.IsNovel); Assert.Equal(1, judge.Calls);
    }

    [Fact]
    public async Task ExactDuplicatesNeverReachOptionalProviders()
    {
        var provider = new ConstantEmbeddingClient(); var judge = new ScriptedNoveltyJudge(ProgramNoveltyVerdict.Novel);
        var policy = new ProgramNoveltyPolicy(new(structuralNoveltyThreshold: 0), embeddingClient: provider, judge: judge);
        var decision = await policy.EvaluateAsync(A(), new[] { A() });
        Assert.False(decision.IsNovel); Assert.True(decision.WasFree); Assert.Equal(0, provider.Calls); Assert.Equal(0, judge.Calls);
    }

    [Fact]
    public async Task CachedEmbeddingComparisonsDoNotInventRequestCost()
    {
        var provider = new ConstantEmbeddingClient(); var policy = new ProgramNoveltyPolicy(embeddingClient: provider);
        var first = await policy.EvaluateAsync(B(), new[] { A() });
        var second = await policy.EvaluateAsync(B(), new[] { A() });
        Assert.Equal(1, first.EmbeddingRequests); Assert.Equal(0, second.EmbeddingRequests);
        Assert.Equal(1, policy.EmbeddingRequests); Assert.Equal(1, policy.FreeDecisions);
    }

    [Fact]
    public async Task RejectedOptionalChecksCarryTheirActualWorkCost()
    {
        var gate = new NoveltyGatingProgramFitnessEvaluator(new RecordingProgramFitnessEvaluator(),
            new ProgramNoveltyPolicy(embeddingClient: new ConstantEmbeddingClient(), judge: new ScriptedNoveltyJudge(ProgramNoveltyVerdict.NotNovel)));
        gate.Remember(A()); var result = await gate.EvaluateAsync(B(), Context());
        Assert.Equal(EvolutionEvaluationStatus.Rejected, result.Status); Assert.Equal(2, result.CostUnits);
        Assert.Equal(0, gate.AcceptedCount);
    }

    [Fact]
    public async Task FailedInnerResultDoesNotPoisonFutureRetries()
    {
        int calls = 0;
        var gate = new NoveltyGatingProgramFitnessEvaluator(new DelegateProgramFitnessEvaluator((_, _, _) =>
            new ValueTask<EvolutionTaskResult>(++calls == 1 ? EvolutionTaskResult.Failed("fixture", "failed") :
                new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1))));
        await gate.EvaluateAsync(A(), Context());
        Assert.Equal(0, gate.TrackedCount);
        Assert.Equal(EvolutionEvaluationStatus.Completed, (await gate.EvaluateAsync(A(), Context())).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentDuplicatesReachInnerOnlyOnce()
    {
        int calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new NoveltyGatingProgramFitnessEvaluator(new DelegateProgramFitnessEvaluator(async (_, _, _) =>
        { Interlocked.Increment(ref calls); started.SetResult(); await release.Task; return new(EvolutionEvaluationStatus.Completed, 1); }));
        var first = gate.EvaluateAsync(A(), Context()).AsTask(); await started.Task;
        var second = gate.EvaluateAsync(B(), Context()).AsTask();
        Assert.Throws<InvalidOperationException>(() => gate.Reset());
        release.SetResult(); var results = await Task.WhenAll(first, second);
        Assert.Equal(1, calls); Assert.Equal(EvolutionEvaluationStatus.Rejected, results[1].Status);
        Assert.Single(gate.GetRememberedGenomes()); gate.Reset(); Assert.Empty(gate.GetRememberedGenomes());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task InvalidCustomDistanceCannotFailOpen(double value)
    {
        var policy = new ProgramNoveltyPolicy(structuralDistance: new InvalidDistance(value));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await policy.EvaluateAsync(B(), new[] { A() }));
    }

    [Fact]
    public async Task EmbeddingCacheUsesExactSourceAndBoundedCapacity()
    {
        var client = new CapturingEmbeddingClient(); var metric = new EmbeddingCosineGenomeDistance(client, cacheCapacity: 2);
        var crlf = new ProgramGenome(A().Source.Replace("\n", "\r\n"));
        await metric.PrimeAsync(new[] { A(), crlf });
        Assert.Equal(2, client.Texts.Count); Assert.NotEqual(client.Texts[0], client.Texts[1]);
        await metric.PrimeAsync(new[] { B() }); Assert.Equal(2, metric.PrimedCount);
        Assert.False(metric.IsPrimed(A()));
    }

    [Fact]
    public async Task ProviderRevisionDriftCannotReuseVectors()
    {
        var client = new CapturingEmbeddingClient(); var metric = new EmbeddingCosineGenomeDistance(client);
        await metric.PrimeAsync(new[] { A() }); client.VersionHash = "v2";
        Assert.Throws<InvalidOperationException>(() => metric.IsPrimed(A()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await metric.PrimeAsync(new[] { B() }));
    }

    [Fact]
    public async Task MalformedEmbeddingBatchHonorsFailClosed()
    {
        var client = new CapturingEmbeddingClient { Malformed = true };
        var policy = new ProgramNoveltyPolicy(new(failOpenOnEmbeddingFailure: false), embeddingClient: client);
        var decision = await policy.EvaluateAsync(B(), new[] { A() });
        Assert.False(decision.IsNovel); Assert.Equal(1, decision.CostUnits);
    }

    [Fact]
    public async Task CanceledNoveltyProviderCannotReturnSuccess()
    {
        using var cts = new CancellationTokenSource();
        var client = new CapturingEmbeddingClient { OnCall = cts.Cancel };
        var metric = new EmbeddingCosineGenomeDistance(client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await metric.PrimeAsync(new[] { A() }, cts.Token));
        Assert.Equal(0, metric.PrimedCount); Assert.Equal(1, metric.PrimeRequests);
    }

    [Fact]
    public async Task NoveltyJudgeHasPinnedIdentityAndStableSeed()
    {
        var first = new FakeChatClient("NOVEL"); var second = new FakeChatClient("NOVEL");
        var judge = new LlmProgramNoveltyJudge(first); var other = new LlmProgramNoveltyJudge(second, temperature: .1);
        Assert.NotEqual(judge.VersionHash, other.VersionHash);
        await judge.JudgeAsync(A(), B()); await new LlmProgramNoveltyJudge(second).JudgeAsync(A(), B());
        Assert.NotNull(first.LastOptions!.Seed); Assert.Equal(first.LastOptions.Seed, second.LastOptions!.Seed);
    }

    [Fact]
    public async Task ConcurrentPrimingSendsOneRequestAndClearInvalidatesInflightResults()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new BlockingEmbeddingClient(started, release);
        var metric = new EmbeddingCosineGenomeDistance(provider);
        var first = metric.PrimeAsync(new[] { A() }).AsTask(); await started.Task;
        var second = metric.PrimeAsync(new[] { A() }).AsTask(); release.SetResult();
        Assert.All(await Task.WhenAll(first, second), Assert.True); Assert.Equal(1, metric.PrimeRequests);

        var started2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = new EmbeddingCosineGenomeDistance(new BlockingEmbeddingClient(started2, release2));
        var pending = other.PrimeAsync(new[] { A() }).AsTask(); await started2.Task;
        other.Clear(); release2.SetResult(); Assert.False(await pending); Assert.Equal(0, other.PrimedCount);
    }

    [Fact]
    public void TruncatedTailCannotBeSpoofedByAnActualSourceLine()
    {
        string tail = "b\nc";
        Assert.True(ProgramLineEditDistance.ComputeDistance("a\n" + tail,
            "a\ntail:" + EvolutionHash.Compute(tail), maxComparedLines: 2) > 0);
    }

    [Fact]
    public async Task FatalNoveltyJudgeErrorsAreNotConvertedToFailOpen()
    {
        var provider = new FakeChatClient("NOVEL") { ThrowOnFirstCall = new OutOfMemoryException() };
        var policy = new ProgramNoveltyPolicy(judge: new LlmProgramNoveltyJudge(provider));
        await Assert.ThrowsAsync<OutOfMemoryException>(async () => await policy.EvaluateAsync(B(), new[] { A() }));
    }

    [Fact]
    public async Task AdmissionPreservesMeasuredMetadataAndChargesBothStages()
    {
        var measured = new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, .3, EvolutionOptimizationDirection.Minimize,
            new Dictionary<string, double> { ["d"] = 2 }, new[] { .3 }, new[] { .1 }, 7,
            new[] { new EvolutionDiagnostic("fixture", "safe") }, new Dictionary<string, double> { ["m"] = 4 },
            new[] { new EvolutionArtifact("note", "fixture") });
        var gate = new NoveltyGatingProgramFitnessEvaluator(new DelegateProgramFitnessEvaluator((_, _, _) => new(measured)),
            new ProgramNoveltyPolicy(embeddingClient: new ConstantEmbeddingClient(), judge: new ScriptedNoveltyJudge(ProgramNoveltyVerdict.Novel)));
        gate.Remember(A()); var result = await gate.EvaluateAsync(B(), Context());
        Assert.Equal(9, result.CostUnits); Assert.Equal(.3, result.Quality); Assert.Equal(measured.Direction, result.Direction);
        Assert.Equal(2, result.Descriptors["d"]); Assert.Equal(4, result.Metrics["m"]);
        Assert.Equal(measured.Objectives, result.Objectives); Assert.Equal(measured.ConstraintViolations, result.ConstraintViolations);
        Assert.Equal(measured.Diagnostics, result.Diagnostics); Assert.Equal(measured.Artifacts, result.Artifacts);
    }

    private sealed class BlockingEmbeddingClient(TaskCompletionSource started, TaskCompletionSource release) : IProgramEmbeddingClient
    {
        public string ModelId => "blocking"; public string VersionHash => "v1";
        public async ValueTask<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        { started.TrySetResult(); await release.Task; return EmbeddingBatch.Success(texts.Select(_ => new EmbeddingVector(new[] { 1d, 0d }))); }
    }

    private sealed class InvalidDistance(double value) : IGenomeDistance<ProgramGenome>
    { public string Id => "bad"; public string VersionHash => "v1"; public double Distance(ProgramGenome a, ProgramGenome b) => value; }
    private sealed class CapturingEmbeddingClient : IProgramEmbeddingClient
    {
        public string ModelId => "capture";
        public string VersionHash { get; set; } = "v1";
        public List<string> Texts { get; } = new();
        public bool Malformed { get; init; }
        public Action? OnCall { get; init; }
        public ValueTask<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Texts.AddRange(texts); OnCall?.Invoke();
            return new(EmbeddingBatch.Success(Enumerable.Range(0, Malformed ? 1 : texts.Count).Select(_ => new EmbeddingVector(new[] { 1d, 0d }))));
        }
    }
}
