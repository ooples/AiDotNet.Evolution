using YamlDotNet.RepresentationModel;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class StoryPullRequestWorkflowTests
{
    // A PR into a branch missing from a pull_request branch list gets no CI at all, so a green stacked PR may never have
    // been built. That is how US-12's code missed main (#54 merged into a feature branch). Every PR must run the gates.
    [Theory]
    [InlineData("build.yml")]
    [InlineData("codeql.yml")]
    [InlineData("dependency-review.yml")]
    [InlineData("sonarcloud.yml")]
    [InlineData("typescript-binding.yml")]
    public void Every_pr_runs_the_gates_without_expanding_push_triggers(string filename)
    {
        var triggers = Triggers(Load(filename));
        Assert.True(triggers.Children.TryGetValue(new YamlScalarNode("pull_request"), out var pullRequest));
        if (pullRequest is YamlMappingNode filters)
        {
            Assert.False(filters.Children.ContainsKey(new YamlScalarNode("branches")));
            Assert.False(filters.Children.ContainsKey(new YamlScalarNode("branches-ignore")));
        }
        else
        {
            Assert.True(pullRequest is YamlScalarNode { Value: null or "" }, "pull_request must be an empty trigger or a mapping without branch filters");
        }

        if (triggers.Children.TryGetValue(new YamlScalarNode("push"), out var push))
            Assert.Equal(new[] { "main" }, Branches(Assert.IsType<YamlMappingNode>(push)));
        Assert.False(triggers.Children.ContainsKey(new YamlScalarNode("pull_request_target")));
    }

    // GitHub cancels a *pending* run whenever a newer run joins its concurrency group, even with cancel-in-progress off.
    // With one group per ref, three merges in a minute left the middle one's run cancelled before any job started, so
    // that merge commit was never certified (11 of 30 main runs before this). Each push to main needs its own group.
    [Theory]
    [InlineData("build.yml")]
    [InlineData("codeql.yml")]
    public void Each_push_to_main_gets_its_own_concurrency_group(string filename)
    {
        var root = Load(filename);
        var concurrency = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("concurrency")]);
        var group = Assert.IsType<YamlScalarNode>(concurrency.Children[new YamlScalarNode("group")]).Value ?? "";
        Assert.Contains("github.event_name == 'push' && github.sha", group);
        var cancel = Assert.IsType<YamlScalarNode>(concurrency.Children[new YamlScalarNode("cancel-in-progress")]).Value ?? "";
        Assert.Contains("github.event_name != 'push'", cancel);
    }

    private static YamlMappingNode Load(string filename)
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiDotNet.Evolution.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("AiDotNet.Evolution.slnx not found above the test directory.");
        using var reader = File.OpenText(Path.Combine(directory.FullName, ".github", "workflows", filename));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static YamlMappingNode Triggers(YamlMappingNode root) =>
        Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("on")]);

    private static string?[] Branches(YamlMappingNode trigger) =>
        Assert.IsType<YamlSequenceNode>(trigger.Children[new YamlScalarNode("branches")])
            .Children.Select(node => Assert.IsType<YamlScalarNode>(node).Value).ToArray();
}
