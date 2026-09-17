using AiDotNet.Evolution.Performance;
using BenchmarkDotNet.Running;

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
