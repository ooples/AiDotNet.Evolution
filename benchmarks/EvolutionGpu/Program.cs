using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AiDotNet.Evolution;
using AiDotNet.Tensors.Engines.DirectGpu.CUDA;

// Trusted, fixed existing kernels only. No generated code, provider calls or production mutation.
if (args is ["--self-test"])
{
    ProofRules.SelfTest();
    return 0;
}
if (args.Length != 1 || File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EvolutionGpu <new-report.json>");
    return 2;
}
using var backend = new CudaBackend();
if (!backend.IsAvailable) throw new InvalidOperationException("A real CUDA device is required; no CPU fallback.");
var campaignClock = Stopwatch.StartNew();
var rows = new List<object>();
foreach (var shape in new[] { (M: 1, K: 64, N: 64), (M: 16, K: 256, N: 256), (M: 64, K: 256, N: 256) })
{
    var observations = new List<object>();
    var space = new EvolutionSearchSpaceBuilder()
        .Add(EvolutionParameter.Categorical("tactic", new[] { "fused", "cublas-unfused" })).Build();
    var task = new EvolutionSearchTask(space, "cuda-linear-gelu", "v1", "oracle-tanh-gelu-v1", (genome, _, cancellation) =>
    {
        cancellation.ThrowIfCancellationRequested();
        var measurement = Measure(genome.Category("tactic"), 7123, 15);
        observations.Add(new { Tactic = genome.Category("tactic"), measurement });
        return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(-measurement.MedianUs,
            new Dictionary<string, double> { ["cell"] = 0 }, costUnits: 1));
    });
    var initial = new[] { "fused", "cublas-unfused" }.Select(tactic => space.CreateGenome(new[]
    {
        new KeyValuePair<string, EvolutionParameterValue>("tactic", EvolutionParameterValue.Categorical(tactic))
    })).ToArray();
    var ledger = new EvolutionResourceLedger("gpu-screen", EvolutionResources.Of("cost_units", 2));
    var engine = new EvolutionEngine<EvolutionSearchGenome>(
        new ResourceMeteredEvolutionTask<EvolutionSearchGenome>(task, ledger, new[] { 1m }),
        EvolutionSearchPresets.CreateAdaptiveMixed(space),
        _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("cell", 0, 1, 1) }),
        new EvolutionEngineOptions
        {
            RunId = "gpu-screen",
            Seed = 7123,
            MaxEvaluationAttempts = 2,
            MaxProposals = 2,
            ProposalBatchSize = 1,
            MaxDegreeOfParallelism = 1,
            MigrationInterval = 0
        });
    var run = await engine.RunAsync(initial);
    if (run.Best is null || observations.Count != 2) throw new InvalidOperationException("Incomplete GPU screening; do not promote.");
    string selected = run.Best.Candidate.CanonicalGenome.Genome.Category("tactic");
    // New values/seeds and independent allocations; alternate ordering to reduce systematic drift.
    var confirmations = new List<object>();
    var ratios = new List<double>();
    for (int replicate = 0; replicate < 5; replicate++)
    {
        int seed = 9811 + replicate;
        var first = Measure(replicate % 2 == 0 ? "fused" : selected, seed, 31);
        var second = Measure(replicate % 2 == 0 ? selected : "fused", seed, 31);
        var baseline = replicate % 2 == 0 ? first : second;
        var candidate = replicate % 2 == 0 ? second : first;
        double ratio = baseline.MedianUs / candidate.MedianUs;
        ratios.Add(ratio);
        confirmations.Add(new { Seed = seed, BaselineFirst = replicate % 2 == 0, Baseline = baseline, Candidate = candidate, Speedup = ratio });
    }
    bool promote = selected != "fused" && ProofRules.ShouldPromote(ratios);
    rows.Add(new
    {
        shape.M,
        shape.K,
        shape.N,
        Baseline = "fused",
        Selected = selected,
        PromotionEligible = promote,
        EffectiveTactic = promote ? selected : "fused",
        Screening = observations,
        Confirmation = confirmations,
        ResidentBufferBytes = sizeof(float) * (long)(shape.M * shape.K + shape.K * shape.N + shape.N + shape.M * shape.N),
        EvaluationCostUnits = ledger.Snapshot().Spent["cost_units"],
        run.StateHash
    });

    Measurement Measure(string tactic, int seed, int samples)
    {
        var random = new Random(seed);
        float[] Values(int count) => Enumerable.Range(0, count).Select(_ => (float)(random.NextDouble() * 0.25 - 0.125)).ToArray();
        float[] a = Values(shape.M * shape.K), b = Values(shape.K * shape.N), bias = Values(shape.N);
        using var input = backend.AllocateBuffer(a);
        using var weight = backend.AllocateBuffer(b);
        using var biasBuffer = backend.AllocateBuffer(bias);
        using var output = backend.AllocateBuffer(shape.M * shape.N);
        void Launch()
        {
            if (tactic == "fused") backend.FusedLinearGELU(input, weight, biasBuffer, output, shape.M, shape.K, shape.N);
            else if (tactic == "cublas-unfused")
            {
                backend.Gemm(input, weight, output, shape.M, shape.N, shape.K);
                backend.BiasAdd(output, biasBuffer, output, shape.M, shape.N);
                backend.Gelu(output, output, shape.M * shape.N);
            }
            else throw new ArgumentException("Unknown tactic.");
        }
        Launch();
        backend.Synchronize();
        double maxError = Check(backend.DownloadBuffer(output));
        for (int i = 0; i < 10; i++) Launch();
        backend.Synchronize();
        const int launches = 16;
        var raw = new double[samples];
        for (int sample = 0; sample < samples; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < launches; i++) Launch();
            backend.Synchronize();
            raw[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / launches;
            if (!double.IsFinite(raw[sample]) || raw[sample] <= 0) throw new InvalidOperationException("Invalid timing.");
        }
        maxError = Math.Max(maxError, Check(backend.DownloadBuffer(output)));
        double median = raw.OrderBy(value => value).ElementAt(raw.Length / 2);
        return new Measurement(median, raw, maxError, launches, 1 + 10 + samples * launches);

        double Check(float[] actual)
        {
            if (actual.Length != shape.M * shape.N) throw new InvalidOperationException("Wrong result length.");
            double maximum = 0;
            for (int m = 0; m < shape.M; m++)
                for (int n = 0; n < shape.N; n++)
                {
                    double value = bias[n];
                    for (int k = 0; k < shape.K; k++) value += (double)a[m * shape.K + k] * b[k * shape.N + n];
                    double expected = 0.5 * value * (1 + Math.Tanh(Math.Sqrt(2 / Math.PI) * (value + 0.044715 * value * value * value)));
                    double error = Math.Abs(actual[m * shape.N + n] - expected);
                    if (!ProofRules.Correct(actual[m * shape.N + n], expected))
                        throw new InvalidOperationException($"Numerical oracle rejected {tactic} at [{m},{n}]: {error}.");
                    maximum = Math.Max(maximum, error);
                }
            return maximum;
        }
    }
}
var assembly = typeof(CudaBackend).Assembly;
using var report = new FileStream(args[0], FileMode.CreateNew, FileAccess.Write);
JsonSerializer.Serialize(report, new
{
    Schema = "evolution-gpu-consumer-v1",
    Device = backend.DeviceName,
    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    ElapsedSeconds = campaignClock.Elapsed.TotalSeconds,
    ScreeningEvaluations = 6,
    ConfirmationMeasurements = 30,
    TotalTimedAndWarmupOperations = 16716,
    TensorsAssembly = assembly.FullName,
    TensorsModuleId = assembly.ManifestModule.ModuleVersionId,
    TensorsAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
    Timing = "resident synchronized host elapsed per operation; includes dispatch, excludes allocation/transfers/JIT warmup",
    Scope = "two existing tactics, three shapes, one GPU; not generated PTX, device-only timing, or OpenEvolve superiority",
    PromotionRule = "all five fresh confirmation speedups exceed 1.05; otherwise retain fused baseline; report only, no production dispatch mutation",
    Work = rows
}, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"GPU evidence written: {args[0]}");
return 0;

internal sealed record Measurement(double MedianUs, double[] SamplesUs, double MaxAbsoluteError, int LaunchesPerSample, int TotalOperations)
{
    public double ThroughputOperationsPerSecond => 1e6 / MedianUs;
}
