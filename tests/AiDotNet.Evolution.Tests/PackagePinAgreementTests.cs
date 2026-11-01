using System.Xml.Linq;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class PackagePinAgreementTests
{
    // Dependabot only bumps projects the solution lists, so #363 moved AiDotNet to 0.235.0 everywhere but in
    // eng/fixtures/DeploymentConsumer and benchmarks/EvolutionNoise. The consumer then asked for 0.234.0 below the
    // Deployment package's 0.235.0 floor, NU1605 failed Pack and validate NuGet, and main went red (#364).
    [Fact]
    public void Every_project_file_pins_a_package_at_the_same_version()
    {
        var root = RepositoryRoot();
        var disagreements = ProjectFiles(root)
            .SelectMany(path => Pins(File.ReadAllText(path)).Select(pin => (pin.Id, pin.Version, Path: Relative(root, path))))
            .GroupBy(pin => pin.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(pin => pin.Version).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key + ": " + string.Join(", ", group.OrderBy(pin => pin.Path, StringComparer.Ordinal)
                .Select(pin => pin.Version + " in " + pin.Path)))
            .ToList();

        Assert.True(disagreements.Count == 0, string.Join(Environment.NewLine, disagreements));
    }

    // Restore ignores a commented-out reference and reads a child <Version> element like the attribute, so the scan does too.
    [Fact]
    public void A_pin_is_read_from_either_version_form_and_never_from_a_comment()
    {
        const string project = """
            <Project>
              <ItemGroup>
                <!-- <PackageReference Include="Old" Version="0.1.0" /> -->
                <PackageReference Include="Attribute" Version="1.0.0" />
                <PackageReference Include="Element"><Version>2.0.0</Version></PackageReference>
                <PackageReference Include="Property" Version="$(SomeVersion)" />
                <PackageReference Update="Updated" Version="3.0.0" />
              </ItemGroup>
            </Project>
            """;

        Assert.Equal(
            new[] { ("Attribute", "1.0.0"), ("Element", "2.0.0"), ("Updated", "3.0.0") },
            Pins(project).OrderBy(pin => pin.Id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_pin_is_read_whatever_the_attribute_order()
    {
        var pins = Pins(
            "<Project>" +
            "<PackageReference Include=\"A\" Version=\"1.0.0\" />" +
            "<PackageReference Version=\"2.0.0\" Include=\"B\" />" +
            "<PackageReference Include=\"C\" PrivateAssets=\"all\" Version=\"3.0.0\" />" +
            "<PackageReference Include=\"D\" Version=\"$(DVersion)\" />" +
            "<PackageReference Include=\"E\" />" +
            "</Project>").ToList();

        Assert.Equal(new[] { ("A", "1.0.0"), ("B", "2.0.0"), ("C", "3.0.0") }, pins);
    }

    // The literal pins of a project file, read as XML so attribute order, comments and a child <Version> element are
    // handled as restore handles them. A version taken from an MSBuild property, or none, is not a pin this test can compare.

    private static IEnumerable<(string Id, string Version)> Pins(string project) =>
        XDocument.Parse(project).Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (
                Id: (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? "",
                Version: (string?)element.Attribute("Version")
                    ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value.Trim() ?? ""))
            .Where(pin => pin.Id.Length > 0 && pin.Version.Length > 0 && pin.Version.IndexOf('$') < 0);

    [Fact]
    public void The_scan_reaches_projects_the_solution_does_not_list()
    {
        var root = RepositoryRoot();
        var scanned = ProjectFiles(root).Select(path => Relative(root, path)).ToList();

        Assert.Contains("eng/fixtures/DeploymentConsumer/DeploymentConsumer.csproj", scanned);
        Assert.Contains("benchmarks/EvolutionNoise/EvolutionNoise.csproj", scanned);
    }

    private static IEnumerable<string> ProjectFiles(string root) =>
        new[] { "src", "tests", "benchmarks", "examples", "eng" }
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or "node_modules"));

    private static string Relative(string root, string path) =>
        path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiDotNet.Evolution.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("AiDotNet.Evolution.slnx not found above the test directory.");
    }
}
