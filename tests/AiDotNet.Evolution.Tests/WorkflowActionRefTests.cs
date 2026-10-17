using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-81 (#327): two steps of one action in one workflow must pin the same ref.
/// </summary>
/// <remarks>
/// Main went red on CodeQL at 3847dbf with "Loaded a configuration file for version '4.38.2', but
/// running version '4.38.3'" because dependabot bumped codeql-action/init (#317) and
/// codeql-action/analyze (#318) in separate pull requests that merged minutes apart: between the two
/// merges codeql.yml ran init at 2892aa5e and analyze at 24c54180. Grouping the action updates (#324)
/// stops dependabot from splitting them, but nothing stopped a hand edit from doing the same, and the
/// failure surfaces only as a run on main - after the push, and as a configuration error rather than
/// a finding. This is the check that fails in the pull request instead.
/// </remarks>
public sealed class WorkflowActionRefTests
{
    [Fact]
    public void RepositoryWorkflowsPinEachActionAtOneRef()
    {
        IReadOnlyList<string> errors = WorkflowActionRefContract.Validate(WorkflowDirectory());

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// The positive control: the exact drift that reddened main is rejected. Without it the rule above
    /// could be satisfied by a validator that never reports anything.
    /// </summary>
    [Fact]
    public void TwoRefsForOneActionInOneWorkflowAreRejected()
    {
        using var fixture = WorkflowFixture.Create();
        string path = Path.Combine(fixture.WorkflowDirectory, "codeql.yml");
        string workflow = File.ReadAllText(path).Replace("\r\n", "\n");
        const string step = "github/codeql-action/init@";
        int at = workflow.IndexOf(step, StringComparison.Ordinal);
        Assert.True(at >= 0, "codeql.yml no longer pins an init step, so this control measures nothing");
        int start = at + step.Length;
        int end = start;
        while (end < workflow.Length && !char.IsWhiteSpace(workflow[end]))
        {
            end++;
        }

        Assert.True(end > start, "the init step has an empty ref");
        string pinned = workflow.Substring(start, end - start);
        // The ref init carries is READ, not written down here: writing today's SHA into this test
        // would make a routine codeql-action bump fail the control instead of the drift it exists
        // to catch. The drifted value is 4.38.2, the stale half of the pair that reddened main.
        const string drifted = "2892aa5e19bbd11bc0cff5427e3b750a04d9e3c2";
        Assert.NotEqual(drifted, pinned);
        File.WriteAllText(path, workflow.Replace(step + pinned, step + drifted));

        IReadOnlyList<string> errors = WorkflowActionRefContract.Validate(fixture.WorkflowDirectory);

        Assert.Contains(errors, error =>
            error.IndexOf("codeql.yml", StringComparison.Ordinal) >= 0 &&
            error.IndexOf("github/codeql-action", StringComparison.Ordinal) >= 0 &&
            error.IndexOf("2892aa5e19bbd11bc0cff5427e3b750a04d9e3c2", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The rule is per workflow file, because that is the scope the drift breaks: one run loads one
    /// configuration. Two files at different refs are untidy but work, so rejecting that here would
    /// fail a repository state which is not the defect.
    /// </summary>
    [Fact]
    public void OneActionAtDifferentRefsInDifferentWorkflowsIsAccepted()
    {
        using var fixture = WorkflowFixture.Create();
        File.WriteAllText(Path.Combine(fixture.WorkflowDirectory, "first.yml"), Workflow("1111111"));
        File.WriteAllText(Path.Combine(fixture.WorkflowDirectory, "second.yml"), Workflow("2222222"));

        IReadOnlyList<string> errors = WorkflowActionRefContract.Validate(fixture.WorkflowDirectory);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    /// <summary>A trailing version comment is not a ref, and a commented-out step is not a step.</summary>
    [Fact]
    public void CommentsAreNotActionReferences()
    {
        using var fixture = WorkflowFixture.Create();
        File.WriteAllText(Path.Combine(fixture.WorkflowDirectory, "comments.yml"),
            """
            name: Harmless comments
            on: workflow_dispatch
            jobs:
              inspect:
                runs-on: ubuntu-latest
                steps:
                  # uses: example/action@0000000
                  - uses: example/action@1111111 # v1, and this comment is not a second ref
                  - run: echo "comments are not workflow structure"
            """);

        IReadOnlyList<string> errors = WorkflowActionRefContract.Validate(fixture.WorkflowDirectory);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// The same action under two sub-paths is one action: this is what init and analyze are, and a rule
    /// keyed on the whole `uses` value would have passed the commit that reddened main.
    /// </summary>
    [Fact]
    public void SubPathsOfOneActionShareTheRefRequirement()
    {
        using var fixture = WorkflowFixture.Create();
        File.WriteAllText(Path.Combine(fixture.WorkflowDirectory, "subpaths.yml"),
            """
            name: Sub-paths
            on: workflow_dispatch
            jobs:
              inspect:
                runs-on: ubuntu-latest
                steps:
                  - uses: example/action/init@1111111
                  - uses: example/action/analyze@2222222
            """);

        IReadOnlyList<string> errors = WorkflowActionRefContract.Validate(fixture.WorkflowDirectory);

        Assert.Contains(errors, error => error.IndexOf("example/action", StringComparison.Ordinal) >= 0);
    }

    /// <summary>A local reusable workflow has no ref to drift, so it is not held to the rule.</summary>
    [Fact]
    public void LocalReusableWorkflowCallsAreNotActionReferences()
    {
        using var fixture = WorkflowFixture.Create();
        File.WriteAllText(Path.Combine(fixture.WorkflowDirectory, "local.yml"),
            """
            name: Local calls
            on: workflow_dispatch
            jobs:
              one:
                uses: ./.github/workflows/attest-release.yml
              two:
                uses: ./.github/workflows/automated-release.yml
            """);

        IReadOnlyList<string> errors = WorkflowActionRefContract.Validate(fixture.WorkflowDirectory);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    /// <summary>A directory with no workflows satisfies every rule, so it has to be reported.</summary>
    [Fact]
    public void AWorkflowDirectoryWithNothingInItIsReported()
    {
        string empty = Path.Combine(Path.GetTempPath(), "aidotnet-evolution-workflow-ref-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            IReadOnlyList<string> errors = WorkflowActionRefContract.Validate(empty);

            Assert.Contains(errors, error => error.IndexOf("no workflows", StringComparison.Ordinal) >= 0);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    private static string Workflow(string reference) =>
        "name: Pinned\n" +
        "on: workflow_dispatch\n" +
        "jobs:\n" +
        "  inspect:\n" +
        "    runs-on: ubuntu-latest\n" +
        "    steps:\n" +
        "      - uses: example/action@" + reference + "\n";

    private static string WorkflowDirectory()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ".github", "workflows");
            if (File.Exists(Path.Combine(candidate, "codeql.yml")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository workflow directory.");
    }

    private sealed class WorkflowFixture : IDisposable
    {
        private WorkflowFixture(string root)
        {
            Root = root;
            WorkflowDirectory = Path.Combine(root, ".github", "workflows");
        }

        private string Root { get; }

        public string WorkflowDirectory { get; }

        public static WorkflowFixture Create()
        {
            string root = Path.Combine(Path.GetTempPath(), "aidotnet-evolution-workflow-ref-tests",
                Guid.NewGuid().ToString("N"));
            var fixture = new WorkflowFixture(root);
            Directory.CreateDirectory(fixture.WorkflowDirectory);
            foreach (string source in Directory.EnumerateFiles(WorkflowDirectory(), "*.yml")
                         .Concat(Directory.EnumerateFiles(WorkflowDirectory(), "*.yaml")))
            {
                File.Copy(source, Path.Combine(fixture.WorkflowDirectory, Path.GetFileName(source)));
            }

            return fixture;
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
/// Reads every workflow and reports an action repository that one workflow pins at more than one ref.
/// </summary>
internal static class WorkflowActionRefContract
{
    private const string LocalWorkflowPrefix = "./";
    private const string ContainerPrefix = "docker://";

    public static IReadOnlyList<string> Validate(string workflowDirectory)
    {
        if (string.IsNullOrWhiteSpace(workflowDirectory))
        {
            throw new ArgumentException("Workflow directory is required.", nameof(workflowDirectory));
        }

        var errors = new List<string>();
        if (!Directory.Exists(workflowDirectory))
        {
            errors.Add($"Workflow directory does not exist: {workflowDirectory}");
            return errors;
        }

        List<string> paths = Directory.EnumerateFiles(workflowDirectory, "*.yml")
            .Concat(Directory.EnumerateFiles(workflowDirectory, "*.yaml"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
        {
            errors.Add($"Workflow directory contains no workflows: {workflowDirectory}");
            return errors;
        }

        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            YamlMappingNode? workflow = Load(path, errors);
            if (workflow is null)
            {
                continue;
            }

            var refsByAction = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string uses in References(workflow))
            {
                if (uses.StartsWith(LocalWorkflowPrefix, StringComparison.Ordinal) ||
                    uses.StartsWith(ContainerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int separator = uses.LastIndexOf('@');
                if (separator <= 0)
                {
                    errors.Add($"{name} uses '{uses}' without a ref");
                    continue;
                }

                string action = Repository(uses.Substring(0, separator));
                if (!refsByAction.TryGetValue(action, out SortedSet<string>? refs))
                {
                    refs = new SortedSet<string>(StringComparer.Ordinal);
                    refsByAction.Add(action, refs);
                }

                refs.Add(uses.Substring(separator + 1));
            }

            foreach (KeyValuePair<string, SortedSet<string>> entry in refsByAction
                         .OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (entry.Value.Count > 1)
                {
                    errors.Add($"{name} pins {entry.Key} at {entry.Value.Count} refs " +
                        $"({string.Join(", ", entry.Value)}): every step of one action in one workflow " +
                        "must use the same ref, or the run fails as a configuration error");
                }
            }
        }

        return errors;
    }

    /// <summary>The owner and repository an action ref belongs to - not its sub-path.</summary>
    private static string Repository(string action)
    {
        string[] segments = action.Split('/');
        return segments.Length <= 2 ? action : segments[0] + "/" + segments[1];
    }

    /// <summary>Every `uses` in a workflow: a job calling another workflow, and every step of every job.</summary>
    private static IEnumerable<string> References(YamlMappingNode workflow)
    {
        foreach (YamlMappingNode job in Jobs(workflow))
        {
            string? call = Scalar(job, "uses");
            if (call is not null)
            {
                yield return call;
            }

            if (Node(job, "steps") is not YamlSequenceNode steps)
            {
                continue;
            }

            foreach (YamlMappingNode step in steps.Children.OfType<YamlMappingNode>())
            {
                string? uses = Scalar(step, "uses");
                if (uses is not null)
                {
                    yield return uses;
                }
            }
        }
    }

    private static IEnumerable<YamlMappingNode> Jobs(YamlMappingNode workflow)
    {
        return Node(workflow, "jobs") is YamlMappingNode jobs
            ? jobs.Children.Values.OfType<YamlMappingNode>()
            : Enumerable.Empty<YamlMappingNode>();
    }

    private static YamlMappingNode? Load(string path, ICollection<string> errors)
    {
        try
        {
            using var reader = File.OpenText(path);
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                errors.Add($"{Path.GetFileName(path)} must contain one YAML mapping document.");
                return null;
            }

            return root;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlException)
        {
            errors.Add($"{Path.GetFileName(path)} is not valid readable YAML: {exception.Message}");
            return null;
        }
    }

    private static string? Scalar(YamlMappingNode mapping, string key) => (Node(mapping, key) as YamlScalarNode)?.Value;

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
