using System.Globalization;
using System.Text.Json;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.MetaPromptAblation;

/// <summary>
/// V1-53 (#166) criterion 4: a paired dev-partition ablation of meta-prompt evolution, on versus off.
/// </summary>
/// <remarks>
/// <para>
/// Paired by engine seed: each seed runs once per arm, and the arms differ only in whether the variation
/// operator is given a <see cref="ProgramMetaPromptEvolver"/> as its instruction source. Same task, same
/// budget, same seed, same model.
/// </para>
/// <para>
/// This is a C# driver rather than a run file because meta-prompt evolution is not reachable from the CLI:
/// <c>RunCommand</c> has no run-file field for it. Measuring through the CLI would have meant adding that
/// field first; measuring here exercises the shipped API directly and keeps the two pieces of work separate.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // The evaluator and the held-out scorer run model-written programs as host processes, with this machine's files
        // and network, so the operator says so on every run rather than inheriting it.
        bool allowUnsandboxed = args.Contains("--allow-unsandboxed", StringComparer.Ordinal);
        args = args.Where(arg => arg != "--allow-unsandboxed").ToArray();
        if (args.Length < 4)
        {
            Console.Error.WriteLine(
                "Usage: <task-directory> <output.json> <seeds> <evaluations-per-arm> [python] [claude] [model] --allow-unsandboxed");
            return 2;
        }

        if (!allowUnsandboxed)
        {
            Console.Error.WriteLine("error: this ablation runs model-written programs as host processes, with this machine's files " +
                "and network. Pass --allow-unsandboxed on a machine provisioned for that.");
            return 2;
        }

        string taskDirectory = Path.GetFullPath(args[0]);
        string outputPath = args[1];
        if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int seeds) || seeds is < 1 or > 64 ||
            !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out int evaluations) || evaluations is < 2 or > 64)
        {
            Console.Error.WriteLine("error: seeds must be 1..64 and evaluations-per-arm 2..64.");
            return 2;
        }

        string python = args.Length > 4 ? args[4] : (OperatingSystem.IsWindows() ? "python" : "python3");
        string claude = args.Length > 5 ? args[5] : "claude";
        string model = args.Length > 6 ? args[6] : "sonnet";

        string evaluatorSource = File.ReadAllText(Path.Combine(taskDirectory, "evaluator.py"));
        string seedProgram = File.ReadAllText(Path.Combine(taskDirectory, "initial_program.py"));
        string scorer = Path.Combine(taskDirectory, "score_heldout.py");
        if (!File.Exists(scorer))
        {
            Console.Error.WriteLine("error: the task directory has no score_heldout.py, so there is no endpoint to measure.");
            return 2;
        }

        var rows = new List<object>();
        foreach (int seed in Enumerable.Range(0, seeds))
        {
            foreach (MetaPromptArm arm in new[] { MetaPromptArm.Fixed, MetaPromptArm.Evolving })
            {
                ArmResult result = await Arm.RunAsync(arm, seed, evaluations, evaluatorSource, seedProgram,
                    scorer, python, claude, model, allowUnsandboxed).ConfigureAwait(false);
                rows.Add(result.ToRow(seed, arm));
                Console.WriteLine(JsonSerializer.Serialize(rows[^1]));
            }
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(new
        {
            Story = "V1-53 #166 criterion 4: meta-prompt evolution, paired dev-partition ablation",
            Design = "paired by engine seed; the arms differ only in the variation operator's instruction source",
            Endpoint = "heldout_accuracy, scored outside the evaluator by the task's own score_heldout.py",
            Model = new { Provider = "ClaudeCode", Name = model },
            Budget = new { Seeds = seeds, EvaluationsPerArm = evaluations },
            Rows = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
