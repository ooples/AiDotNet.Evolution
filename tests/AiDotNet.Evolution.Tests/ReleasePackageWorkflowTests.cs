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
        JsonElement[] extraFiles = config.RootElement.GetProperty("packages").GetProperty(".")
            .GetProperty("extra-files").EnumerateArray().ToArray();
        string[] configured = extraFiles.Select(file => file.GetProperty("path").GetString()!)
            .Where(path => path.StartsWith("src/", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        // The npm and PyPI packages release in lock-step with NuGet (E-5): every version field they carry, the npm
        // package's pins of its platform hosts included, and nothing else outside src/.
        string[] bindings = extraFiles.Where(file => !file.GetProperty("path").GetString()!.StartsWith("src/", StringComparison.Ordinal))
            .Select(file => file.GetProperty("path").GetString() + " " + file.GetProperty("jsonpath").GetString())
            .OrderBy(entry => entry, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "bindings/python/pyproject.toml $.project.version",
            "bindings/typescript/package.json $.optionalDependencies['@aidotnet/evolution-host-linux-x64']",
            "bindings/typescript/package.json $.optionalDependencies['@aidotnet/evolution-host-osx-arm64']",
            "bindings/typescript/package.json $.optionalDependencies['@aidotnet/evolution-host-win-x64']",
            "bindings/typescript/package.json $.version"
        }, bindings);
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
