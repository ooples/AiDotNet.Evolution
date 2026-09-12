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

if (args.Length == 1 && args[0] == "--smoke")
{
    foreach (int workers in new[] { 1, 4 })
        foreach (var dispatch in new[] { AiDotNet.Evolution.EvolutionDispatchMode.Batch, AiDotNet.Evolution.EvolutionDispatchMode.Continuous })
            foreach (int delay in new[] { 0, 1 })
                foreach (bool checkpoint in new[] { false, true })
                {
                    var fixture = new EngineBenchmarks { Workers = workers, Dispatch = dispatch, EvaluatorDelayMilliseconds = delay, Checkpoint = checkpoint };
                    fixture.Setup(); await fixture.EvaluationOnly(); await fixture.RunEngine();
                }
    foreach (int cells in new[] { 100, 1000, 10000 })
    {
        var fixture = new ArchiveBenchmarks { Cells = cells }; fixture.Setup();
        if (fixture.Lookup() is null || fixture.Sample() is null || fixture.Snapshot().Count != cells)
            throw new InvalidOperationException("Archive performance fixture failed its contract.");
    }
    foreach (int evaluations in new[] { 32, 256, 2048 })
    {
        var fixture = new CheckpointBenchmarks { Evaluations = evaluations }; await fixture.Setup();
        fixture.ChecksumEnvelope().Validate();
        if (await fixture.LoadClone() is null) throw new InvalidOperationException("Checkpoint load failed.");
        await fixture.RestoreWithoutNewEvaluations();
    }
    Console.WriteLine("Performance fixtures passed: 16 engine configurations, 3 archive sizes, 3 checkpoint sizes. No timing claim.");
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(EngineBenchmarks).Assembly).Run(args);
