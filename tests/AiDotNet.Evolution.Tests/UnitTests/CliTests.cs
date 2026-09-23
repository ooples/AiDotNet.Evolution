#if NET8_0_OR_GREATER
using System.Text.Json;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>V1-26: the aidotnet-evolve commands against a real engine trace.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class CliTests
{
    private static async Task<string> Trace(string directory, string name, int attempts)
    {
        string path = Path.Combine(directory, name + ".jsonl");
        var options = new EvolutionEngineOptions { RunId = name, MaxEvaluationAttempts = attempts, MaxProposals = attempts, MaxGenerations = attempts,
            ProposalBatchSize = 1, MaxDegreeOfParallelism = 1, IslandCount = 1, MigrationInterval = 0, CheckpointInterval = 0 };
        using (var tracer = new EvolutionTraceObserver<TestGenome>(new EvolutionTraceOptions { Enabled = true, Path = path }, name))
            await new EvolutionEngine<TestGenome>(new SyntheticEvolutionTask(), new IncrementVariation(),
                _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 100) }), options, observer: tracer)
                .RunAsync(new[] { new TestGenome(1), new TestGenome(2) });
        return path;
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        TextWriter original = Console.Out;
        Console.SetOut(output);
        try { return (AiDotNet.Evolution.Cli.Program.Main(args), output.ToString()); }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public async Task Inspect_export_report_and_compare_work_on_a_real_trace()
    {
        using var directory = new TemporaryDirectory();
        string first = await Trace(directory.Path, "run-a", 30), second = await Trace(directory.Path, "run-b", 12);

        var (inspectCode, inspect) = Run("inspect", first);
        Assert.Equal(0, inspectCode);
        using (JsonDocument summary = JsonDocument.Parse(inspect))
        {
            Assert.True(summary.RootElement.GetProperty("Records").GetInt32() >= 30);
            Assert.NotEqual(JsonValueKind.Null, summary.RootElement.GetProperty("Best").ValueKind);
        }

        string exportDir = Path.Combine(directory.Path, "export");
        Assert.Equal(0, Run("export", first, exportDir).Code);
        using (JsonDocument winner = JsonDocument.Parse(File.ReadAllText(Path.Combine(exportDir, "winner.json"))))
        {
            Assert.Equal("aidotnet-evolve-export-v1", winner.RootElement.GetProperty("Schema").GetString());
            Assert.True(winner.RootElement.GetProperty("Lineage").GetArrayLength() >= 1);
            Assert.Contains("no credentials", winner.RootElement.GetProperty("Limitations").GetString());
        }
        Assert.Equal(2, Run("export", first, exportDir).Code); // never overwrites

        string html = Path.Combine(directory.Path, "report.html");
        Assert.Equal(0, Run("report", first, html).Code);
        string page = File.ReadAllText(html);
        Assert.Contains("<svg", page);
        Assert.Contains("Lineage of the best program", page);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", page);
        Assert.DoesNotContain("https://", page); // self-contained: opens offline

        var (compareCode, compare) = Run("compare", first, second);
        Assert.Equal(0, compareCode);
        using (JsonDocument both = JsonDocument.Parse(compare))
            Assert.True(both.RootElement.GetProperty("A").GetProperty("Records").GetInt32() > both.RootElement.GetProperty("B").GetProperty("Records").GetInt32());
    }

    [Fact]
    public void Bad_usage_and_missing_traces_fail_with_exit_code_2()
    {
        Assert.Equal(2, Run().Code);
        Assert.Equal(2, Run("inspect").Code);
        Assert.Equal(2, Run("inspect", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jsonl")).Code);
    }
}
/// <summary>Tests that redirect the process-wide Console must not run in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}
#endif