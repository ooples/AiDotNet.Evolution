using AiDotNet.Evolution.Performance;
using BenchmarkDotNet.Running;
using System.Globalization;
using System.Text.Json;

if (args.Length > 0 && args[0] == "--profile-worker")
{
    if (args.Length != 5) throw new ArgumentException("Internal worker expects case, new output file, affinity mask and processor group.");
    var input = new FileInfo(args[1]);
    if (!input.Exists || input.Length > 16384) throw new InvalidDataException("Worker case input is missing or oversized.");
    var scenario = JsonSerializer.Deserialize<ProfileCase>(await File.ReadAllTextAsync(input.FullName), ProfileCampaign.JsonOptions)
        ?? throw new InvalidDataException("Worker case cannot be null.");
    var result = await ProfileRunner.MeasureAsync(scenario, ulong.Parse(args[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        ushort.Parse(args[4], CultureInfo.InvariantCulture));
    await ProfileCampaign.WriteNewAsync(args[2], result);
    return;
}

if (args.Length > 0 && args[0] == "--profile")
{
    if (args.Length is < 3 or > 4 || (args.Length == 4 && args[3] != "--smoke"))
        throw new ArgumentException("Usage: --profile <new-output-directory> <40-character-source-revision> [--smoke]");
    var report = await ProfileCampaign.RunAsync(args[1], args[2], args.Length == 4);
    Console.WriteLine($"Profile {report.Status}: {report.Attempts.Count} attempts; {report.DeterministicWorkerGroups} deterministic worker groups. See report.json.");
    Environment.ExitCode = report.Status == "passed" ? 0 : 1;
    return;
}

if (args.Length > 0 && args[0] == "--evidence")
{
    // Compacts one raw report into the committed summary; the raw report itself is published as a release asset.
    if (args.Length is < 3 or > 5) throw new ArgumentException("Usage: --evidence <report.json> <new-summary.json> [<raw-download-url>]");
    var raw = ProfileEvidence.Describe(args[1], args.Length >= 4 ? args[3] : null);
    var report = JsonSerializer.Deserialize<ProfileReport>(await File.ReadAllTextAsync(args[1]), ProfileCampaign.JsonOptions)
        ?? throw new InvalidDataException("The raw report cannot be null.");
    var summary = ProfileEvidence.Compact(report, raw);
    await ProfileCampaign.WriteNewAsync(args[2], summary);
    Console.WriteLine($"Evidence summary written for {summary.Attempts.Count} attempts and {summary.MixedDispatchCurves.Count} published curves; " +
        $"raw {raw.FileName} is {raw.Bytes} bytes, sha256 {raw.Sha256}.");
    return;
}

if (args.Length > 0 && args[0] == "--evidence-failure")
{
    if (args.Length is < 4 or > 5) throw new ArgumentException("Usage: --evidence-failure <failed-report.json> <new-summary.json> <reason> [<raw-download-url>]");
    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
    var failure = ProfileEvidence.CompactFailure(document, args[3], ProfileEvidence.Describe(args[1], args.Length == 5 ? args[4] : null));
    await ProfileCampaign.WriteNewAsync(args[2], failure);
    Console.WriteLine($"Failed-campaign record written: {failure.Divergences.Count} diverging semantic groups over {failure.Attempts} attempts.");
    return;
}

if (args.Length == 2 && args[0] == "--evidence-tables")
{
    var summary = JsonSerializer.Deserialize<ProfileEvidenceSummary>(await File.ReadAllTextAsync(args[1]), ProfileCampaign.JsonOptions)
        ?? throw new InvalidDataException("The evidence summary cannot be null.");
    Console.Write(ProfileEvidence.RenderTables(summary));
    return;
}

if (args.Length == 1 && args[0] == "--smoke")
{
    int engineFixtures = 0, baselineFixtures = 0, archiveFixtures = 0, checkpointFixtures = 0;
    // Dispatch, worker, delay and checkpoint factors, plus one variant of each factor the engine benchmark added.
    var engineConfigurations = new List<EngineBenchmarks>();
    foreach (int workers in new[] { 1, 4 })
        foreach (var dispatch in new[] { AiDotNet.Evolution.EvolutionDispatchMode.Batch, AiDotNet.Evolution.EvolutionDispatchMode.Continuous })
            foreach (int delay in new[] { 0, 1 })
                foreach (bool checkpoint in new[] { false, true })
                    engineConfigurations.Add(new EngineBenchmarks { Workers = workers, Dispatch = dispatch, EvaluatorDelayMilliseconds = delay, Checkpoint = checkpoint });
    engineConfigurations.Add(new EngineBenchmarks { Dimensions = 2 });
    engineConfigurations.Add(new EngineBenchmarks { ArchiveCells = 1000 });
    engineConfigurations.Add(new EngineBenchmarks { Islands = 4 });
    foreach (var fixture in engineConfigurations)
    {
        fixture.Setup();
        if (string.IsNullOrEmpty(await fixture.RunEngine())) throw new InvalidOperationException("Engine performance fixture produced no state hash.");
        engineFixtures++;
    }
    foreach (int workers in new[] { 1, 4 })
        foreach (int delay in new[] { 0, 1 })
            foreach (int dimensions in new[] { 2, 8 })
            {
                var fixture = new EvaluatorBaselineBenchmarks { Workers = workers, EvaluatorDelayMilliseconds = delay, Dimensions = dimensions };
                fixture.Setup(); await fixture.EvaluationOnly(); baselineFixtures++;
            }
    foreach (int cells in new[] { 100, 1000, 10000 })
        foreach (int dimensions in new[] { 1, 8 })
        {
            var fixture = new ArchiveBenchmarks { Cells = cells, Dimensions = dimensions }; fixture.Setup();
            if (fixture.Lookup() is null || fixture.Sample() is null || fixture.Snapshot().Count != cells)
                throw new InvalidOperationException("Archive performance fixture failed its contract.");
            archiveFixtures++;
        }
    foreach (int evaluations in new[] { 32, 256, 2048 })
    {
        var fixture = new CheckpointBenchmarks { Evaluations = evaluations }; await fixture.Setup();
        fixture.ChecksumEnvelope().Validate();
        if (await fixture.LoadClone() is null) throw new InvalidOperationException("Checkpoint load failed.");
        await fixture.RestoreWithoutNewEvaluations();
        checkpointFixtures++;
    }
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"Performance fixtures passed: {engineFixtures} engine configurations (workers, dispatch, delay, checkpoint, dimensions, archive cells, islands), " +
        $"{baselineFixtures} evaluator-only configurations, {archiveFixtures} archive configurations, {checkpointFixtures} checkpoint sizes. No timing claim."));
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(EngineBenchmarks).Assembly).Run(args);
