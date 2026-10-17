using YamlDotNet.RepresentationModel;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-81 (#183): the release job survives a secondary rate limit without going green when it has not run.
/// </summary>
/// <remarks>
/// `release-please` failed on four of the last thirty merges to main with "API rate limit already
/// exceeded for user ID ...", every time AFTER the credentials check in the same job had passed
/// seconds earlier - so it is the account's API quota, not the token, and `/rate_limit` reports
/// quota remaining when it happens, which is why a preflight cannot predict it. One retry recovers
/// the merge, because the action reconciles the same release pull request rather than creating a
/// second one. The danger in writing it that way is the opposite failure: `continue-on-error` on the
/// first attempt plus a condition on the second means that when BOTH fail, every step has either
/// succeeded, continued or been skipped, and the job is green with no release reconciled. These
/// tests pin the parts that stop that.
/// </remarks>
public sealed class ReleasePleaseWorkflowTests
{
    [Fact]
    public void TheReleaseJobRetriesOnceAndFailsWhenNeitherAttemptRan()
    {
        IReadOnlyList<string> errors = ReleasePleaseContract.Validate(WorkflowPath());

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void AJobThatCannotFailWhenBothAttemptsFailIsRejected()
    {
        using var fixture = WorkflowCopy.Create();
        string workflow = File.ReadAllText(fixture.Path).Replace("\r\n", "\n");
        int gate = workflow.IndexOf("      - name: Fail if neither attempt reconciled the release",
            StringComparison.Ordinal);
        Assert.True(gate >= 0, "the workflow no longer has the step this test removes");
        File.WriteAllText(fixture.Path, workflow.Substring(0, gate));

        IReadOnlyList<string> errors = ReleasePleaseContract.Validate(fixture.Path);

        Assert.Contains(errors, error => error.IndexOf("both attempts fail", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void OutputsReadFromOnlyTheFirstAttemptAreRejected()
    {
        using var fixture = WorkflowCopy.Create();
        string workflow = File.ReadAllText(fixture.Path).Replace("\r\n", "\n");
        const string both = "${{ steps.release.outputs.release_created || steps.retry.outputs.release_created }}";
        const string one = "${{ steps.release.outputs.release_created }}";
        Assert.Contains(both, workflow, StringComparison.Ordinal);
        File.WriteAllText(fixture.Path, workflow.Replace(both, one));

        IReadOnlyList<string> errors = ReleasePleaseContract.Validate(fixture.Path);

        Assert.Contains(errors, error => error.IndexOf("release_created", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void AFirstAttemptThatCannotFailSoftlyIsRejected()
    {
        using var fixture = WorkflowCopy.Create();
        string workflow = File.ReadAllText(fixture.Path).Replace("\r\n", "\n");
        const string soft = "        continue-on-error: true\n";
        Assert.Contains(soft, workflow, StringComparison.Ordinal);
        File.WriteAllText(fixture.Path, workflow.Replace(soft, string.Empty));

        IReadOnlyList<string> errors = ReleasePleaseContract.Validate(fixture.Path);

        Assert.Contains(errors, error => error.IndexOf("continue-on-error", StringComparison.Ordinal) >= 0);
    }

    private static string WorkflowPath()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ".github", "workflows", "release-please.yml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate release-please.yml.");
    }

    private sealed class WorkflowCopy : IDisposable
    {
        private WorkflowCopy(string root, string path)
        {
            Root = root;
            Path = path;
        }

        private string Root { get; }

        public string Path { get; }

        public static WorkflowCopy Create()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "aidotnet-evolution-release-please-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path = System.IO.Path.Combine(root, "release-please.yml");
            File.Copy(WorkflowPath(), path);
            return new WorkflowCopy(root, path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

/// <summary>
/// The release job's retry contract: one soft first attempt, one conditional second, and a step that
/// fails the job when neither ran - because without it the all-skipped job is green.
/// </summary>
internal static class ReleasePleaseContract
{
    private const string Action = "googleapis/release-please-action@";
    private const string FirstId = "release";
    private const string SecondId = "retry";
    private const string FirstFailed = "steps.release.outcome == 'failure'";

    public static IReadOnlyList<string> Validate(string workflowPath)
    {
        var errors = new List<string>();
        var stream = new YamlStream();
        using (var reader = File.OpenText(workflowPath))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            errors.Add("release-please.yml must contain one YAML mapping document.");
            return errors;
        }

        if (Node(root, "jobs") is not YamlMappingNode jobs ||
            Node(jobs, "release-please") is not YamlMappingNode job)
        {
            errors.Add("release-please.yml must define a release-please job.");
            return errors;
        }

        List<YamlMappingNode> steps = Node(job, "steps") is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlMappingNode>().ToList()
            : new List<YamlMappingNode>();
        List<YamlMappingNode> attempts = steps
            .Where(step => (Scalar(step, "uses") ?? string.Empty).StartsWith(Action, StringComparison.Ordinal))
            .ToList();
        if (attempts.Count != 2)
        {
            errors.Add($"the job runs the action {attempts.Count} time(s); it must run once and retry once");
            return errors;
        }

        YamlMappingNode first = attempts[0], second = attempts[1];
        if (Scalar(first, "id") != FirstId || Scalar(second, "id") != SecondId)
        {
            errors.Add($"the two attempts must be `{FirstId}` then `{SecondId}`, so the outputs and the "
                + "conditions below can name them");
        }

        if (Scalar(first, "continue-on-error") != "true")
        {
            errors.Add("the first attempt needs continue-on-error: true, or the retry can never run");
        }

        if ((Scalar(second, "if") ?? string.Empty).IndexOf(FirstFailed, StringComparison.Ordinal) < 0)
        {
            errors.Add($"the retry must run only when the first attempt failed ({FirstFailed}), or it "
                + "doubles every release");
        }

        // The job is green when both attempts fail unless one step says otherwise: the first is
        // continue-on-error and the second is skipped by its own condition, so neither is a job failure.
        List<YamlMappingNode> gates = steps
            .Where(step => Scalar(step, "continue-on-error") is null)
            .Where(step => (Scalar(step, "if") ?? string.Empty).IndexOf(FirstFailed, StringComparison.Ordinal) >= 0
                && (Scalar(step, "if") ?? string.Empty).IndexOf("steps.retry.outcome", StringComparison.Ordinal) >= 0)
            .Where(step => (Scalar(step, "run") ?? string.Empty).IndexOf("exit 1", StringComparison.Ordinal) >= 0)
            .ToList();
        if (gates.Count != 1)
        {
            errors.Add("no step fails the job when both attempts fail: one is needed that tests both "
                + "outcomes and exits nonzero");
        }

        foreach (string output in new[] { "release_created", "tag_name" })
        {
            string value = Scalar(Node(job, "outputs") as YamlMappingNode, output) ?? string.Empty;
            if (value.IndexOf("steps." + FirstId + ".outputs." + output, StringComparison.Ordinal) < 0 ||
                value.IndexOf("steps." + SecondId + ".outputs." + output, StringComparison.Ordinal) < 0)
            {
                errors.Add($"the {output} output must fall back across both attempts, or a release the "
                    + "retry created is never published");
            }
        }

        return errors;
    }

    private static string? Scalar(YamlMappingNode? mapping, string key) =>
        (mapping is null ? null : Node(mapping, key) as YamlScalarNode)?.Value;

    private static YamlNode? Node(YamlMappingNode mapping, string key)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
