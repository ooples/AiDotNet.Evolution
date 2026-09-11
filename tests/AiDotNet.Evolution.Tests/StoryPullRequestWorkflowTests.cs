using YamlDotNet.RepresentationModel;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class StoryPullRequestWorkflowTests
{
    [Theory]
    [InlineData("build.yml")]
    [InlineData("codeql.yml")]
    [InlineData("dependency-review.yml")]
    [InlineData("sonarcloud.yml")]
    public void Story_prs_run_existing_gates_without_expanding_push_triggers(string filename)
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiDotNet.Evolution.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        using var reader = File.OpenText(Path.Combine(directory!.FullName, ".github", "workflows", filename));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        var triggers = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("on")]);
        var pullRequest = Assert.IsType<YamlMappingNode>(triggers.Children[new YamlScalarNode("pull_request")]);
        Assert.Equal(new[] { "main", "feat/competitive-evolution-platform" }, Branches(pullRequest));
        if (triggers.Children.TryGetValue(new YamlScalarNode("push"), out var push))
            Assert.Equal(new[] { "main" }, Branches(Assert.IsType<YamlMappingNode>(push)));
        Assert.False(triggers.Children.ContainsKey(new YamlScalarNode("pull_request_target")));
    }

    private static string?[] Branches(YamlMappingNode trigger) =>
        Assert.IsType<YamlSequenceNode>(trigger.Children[new YamlScalarNode("branches")])
            .Children.Select(node => Assert.IsType<YamlScalarNode>(node).Value).ToArray();
}
