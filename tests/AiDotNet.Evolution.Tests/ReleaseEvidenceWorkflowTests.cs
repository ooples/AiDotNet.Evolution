using YamlDotNet.RepresentationModel;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>US-12 (#30): the release claim gate runs where releases are made, and nothing spends on a pull request.</summary>
public sealed class ReleaseEvidenceWorkflowTests
{
    [Fact]
    public void ReleaseValidationEnforcesTheDeclaredClaimBeforeAnythingIsPacked()
    {
        YamlMappingNode jobs = Jobs("automated-release.yml");
        string[] validate = Commands(Job(jobs, "validate"));
        string gate = Assert.Single(validate, command => command.Contains("release_gate.py", StringComparison.Ordinal));
        Assert.Contains(".release-tooling/benchmarks/analysis/release_gate.py", gate, StringComparison.Ordinal);
        Assert.Contains("--version \"$RELEASE_VERSION\"", gate, StringComparison.Ordinal);
        Assert.Contains("validate", Needs(Job(jobs, "pack")));
        Assert.Contains("validate", Needs(Job(jobs, "publish")));
    }

    [Fact]
    public void ReleaseAssetsIncludeTheAssessmentAndTheCompleteBundle()
    {
        YamlMappingNode jobs = Jobs("automated-release.yml");
        string upload = Assert.Single(Commands(Job(jobs, "attach")), command => command.Contains("gh release upload", StringComparison.Ordinal));
        Assert.Contains("evidence/release-evidence.json", upload, StringComparison.Ordinal);
        Assert.Contains("release-evidence-bundle.zip", upload, StringComparison.Ordinal);
    }

    [Fact]
    public void PullRequestsRunOnlyTheClaimGateAndNothingThatSpends()
    {
        string path = WorkflowPath("release-evidence.yml");
        string text = File.ReadAllText(path);
        Assert.DoesNotContain("secrets.", text, StringComparison.Ordinal);
        YamlMappingNode root = Load(path);
        Assert.Equal("read", Scalar(Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("permissions")]), "contents"));
        YamlMappingNode jobs = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("jobs")]);
        foreach (KeyValuePair<YamlNode, YamlNode> job in jobs.Children)
        {
            var mapping = Assert.IsType<YamlMappingNode>(job.Value);
            bool runsComparison = Commands(mapping).Any(command => command.Contains("Run-BudgetedComparison.ps1", StringComparison.Ordinal));
            if (!runsComparison)
            {
                continue;
            }

            string condition = Scalar(mapping, "if") ?? string.Empty;
            Assert.Equal("github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'", condition);
        }

        Assert.Contains(Commands(Job(jobs, "release-claim")), command => command.Contains("release_gate.py", StringComparison.Ordinal));
    }

    private static YamlMappingNode Jobs(string workflow) =>
        Assert.IsType<YamlMappingNode>(Load(WorkflowPath(workflow)).Children[new YamlScalarNode("jobs")]);

    private static YamlMappingNode Job(YamlMappingNode jobs, string name) =>
        Assert.IsType<YamlMappingNode>(jobs.Children[new YamlScalarNode(name)]);

    private static string[] Commands(YamlMappingNode job) =>
        Assert.IsType<YamlSequenceNode>(job.Children[new YamlScalarNode("steps")]).Children
            .Cast<YamlMappingNode>()
            .Select(step => Scalar(step, "run"))
            .Where(command => command is not null)
            .Select(command => command ?? string.Empty)
            .ToArray();

    private static string[] Needs(YamlMappingNode job) =>
        job.Children.TryGetValue(new YamlScalarNode("needs"), out YamlNode? needs) && needs is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlScalarNode>().Select(node => node.Value ?? string.Empty).ToArray()
            : new[] { Scalar(job, "needs") ?? string.Empty };

    private static string? Scalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) ? (node as YamlScalarNode)?.Value : null;

    private static YamlMappingNode Load(string path)
    {
        using var reader = File.OpenText(path);
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static string WorkflowPath(string workflow)
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiDotNet.Evolution.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, ".github", "workflows", workflow);
    }
}