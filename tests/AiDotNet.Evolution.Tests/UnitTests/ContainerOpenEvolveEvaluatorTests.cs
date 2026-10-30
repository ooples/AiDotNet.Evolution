#if NET8_0_OR_GREATER
using System.Text.Json;
using AiDotNet.Evolution.CSharp.Tests.Execution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>An OpenEvolve evaluator runs through <c>aidotnet-evolve run</c> with the CLI's container section.</summary>
/// <remarks>
/// Docker-gated like the engine's own container tests: CI's container lane sets
/// <c>AIDOTNET_EVOLUTION_REQUIRE_DOCKER=1</c>, so there these fail rather than skip.
/// </remarks>
[Collection(ConsoleCollection.Name)]
public sealed class ContainerOpenEvolveEvaluatorTests
{
    // An OpenEvolve-format evaluator (evaluate(program_path) returning metrics with combined_score) that imports a
    // helper from beside it and a module from openEvolveEvaluator.pythonPath, the two imports that need the host's files.
    // It scores -1 unless it is really inside the container: user nobody, its own directory where the CLI mounts it and
    // not writable, and no network. A run that escaped the container therefore cannot reach the expected best score.
    // Like upstream evaluators, it runs the candidate in a fresh interpreter before reading what it defines. (No Python
    // comments inside the literal: on .NET Framework this file is an inactive #if region, where a line starting with
    // '#' is read as a preprocessor directive.)
    private const string Evaluator = """
        import os, re, socket, subprocess, sys
        import pathbonus
        from scoring import score

        def _isolated():
            here = os.path.dirname(os.path.abspath(__file__))
            if os.getuid() != 65534 or here != "/aidotnet/evaluator":
                return False
            try:
                with open(os.path.join(here, "probe"), "w") as probe:
                    probe.write("x")
                return False
            except OSError:
                pass
            try:
                socket.create_connection(("1.1.1.1", 53), timeout=2).close()
                return False
            except OSError:
                return True

        def evaluate(program_path):
            ran = subprocess.run([sys.executable, program_path], capture_output=True, text=True, timeout=30)
            if ran.returncode != 0:
                return {"combined_score": -2.0}
            with open(program_path, encoding="utf-8") as stream:
                match = re.search(r"^X = (\d+)$", stream.read(), re.MULTILINE)
            if not match:
                return {"combined_score": -3.0}
            if not _isolated():
                return {"combined_score": -1.0}
            return {"combined_score": score(int(match.group(1))) + pathbonus.BONUS}
        """;

    [DockerTheory]
    [InlineData(ContainerLifecycle.Warm)]
    [InlineData(ContainerLifecycle.Cold)]
    public void An_openevolve_evaluator_runs_end_to_end_in_a_container(ContainerLifecycle lifecycle)
    {
        using var directory = new TemporaryDirectory();
        using var model = new CliRunTests.FakeChatModel();
        string root = directory.Path;
        string library = Path.Combine(root, "lib");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(root, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(root, "evaluator.py"), Evaluator);
        File.WriteAllText(Path.Combine(root, "scoring.py"), "def score(value):\n    return float(value)\n");
        File.WriteAllText(Path.Combine(library, "pathbonus.py"), "BONUS = 0.5\n");
        // The container runs as nobody, so the mounted directories must be readable by others whatever the umask is.
        if (!OperatingSystem.IsWindows())
        {
            foreach (string folder in new[] { root, library })
                File.SetUnixFileMode(folder, (UnixFileMode)0b111_101_101);
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, (UnixFileMode)0b110_100_100);
        }

        // No allowUnsandboxedExecution: the container section alone is what lets this run start.
        string runFile = Path.Combine(root, "run.json");
        File.WriteAllText(runFile, $$"""
            {
              "schema": "aidotnet-evolve-run-v1",
              "runId": "container-openevolve",
              "initialProgram": "initial.py",
              "evaluator": "evaluator.py",
              "model": { "endpoint": "{{model.Endpoint}}", "name": "fake-model", "timeoutSeconds": 20, "maxRetries": 0 },
              "budget": { "maxEvaluations": 3, "seed": 7, "evaluationTimeLimitSeconds": 30 },
              "output": "out",
              "container": { "image": "{{DockerTestEnvironment.Image}}", "lifecycle": "{{lifecycle}}" },
              "openEvolveEvaluator": { "timeoutSeconds": 30, "pythonPath": [ "lib" ] }
            }
            """);

        var (code, output, error) = CliRunTests.Run("run", runFile);

        Assert.True(code == 0, error);
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.Equal(3, report.RootElement.GetProperty("CompletedEvaluations").GetInt64());
        // The seed X = 0 and the model's X = 1 and X = 2, each scored inside the container as X + 0.5.
        Assert.Equal(2.5d, report.RootElement.GetProperty("BestQuality").GetDouble());
        Assert.Equal("X = 2", File.ReadAllText(Path.Combine(root, "out", "best.py")).TrimEnd());
        Assert.False(File.Exists(Path.Combine(root, "probe")), "the evaluator wrote into its mounted directory");
        (int ExitCode, string StdOut)? left = DockerTestEnvironment.Docker(
            "ps", "--all", "--quiet", "--filter", "label=org.aidotnet.evolution.container");
        Assert.True(left is { ExitCode: 0 }, "docker ps failed");
        Assert.Equal(string.Empty, left.Value.StdOut.Trim());
    }
}
#endif
