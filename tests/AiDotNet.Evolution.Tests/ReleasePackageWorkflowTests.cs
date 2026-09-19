using System.Text.Json;
using System.Xml.Linq;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AiDotNet.Evolution.Tests;

public sealed class ReleasePackageWorkflowTests
{
    [Fact]
    public void ReleasePleaseVersionsEveryPackableSourceProject()
    {
        string root = Root();
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "release-please-config.json")));
        string[] configured = config.RootElement.GetProperty("packages").GetProperty(".")
            .GetProperty("extra-files").EnumerateArray().Select(file => file.GetProperty("path").GetString()!)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        string[] packable = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !XDocument.Load(path).Descendants("IsPackable").Any(value => value.Value == "false"))
            .Select(path => "src/" + new DirectoryInfo(Path.GetDirectoryName(path)!).Name + "/" + Path.GetFileName(path))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(packable, configured);
    }

    [Theory]
    [InlineData("automated-release.yml", "pack")]
    [InlineData("build.yml", "package")]
    public void ReleaseAndCiUseTheSamePackingAndConsumerChecks(string workflow, string jobName)
    {
        var yaml = new YamlStream();
        using var reader = File.OpenText(Path.Combine(Root(), ".github", "workflows", workflow));
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var job = (YamlMappingNode)jobs.Children[new YamlScalarNode(jobName)];
        var steps = (YamlSequenceNode)job.Children[new YamlScalarNode("steps")];
        var commands = steps.Children.Cast<YamlMappingNode>()
            .Where(step => step.Children.ContainsKey(new YamlScalarNode("run")))
            .Select(step => ((YamlScalarNode)step.Children[new YamlScalarNode("run")]).Value!).ToArray();
        Assert.Contains(commands, command => command.Contains("/eng/Pack-Release.ps1"));
        Assert.Contains(commands, command => command.Contains("/eng/Test-ReleaseConsumers.ps1"));
        Assert.DoesNotContain(commands, command => command.Contains("dotnet nuget push"));
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "release-please-config.json"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
