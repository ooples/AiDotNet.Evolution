using System.Text.RegularExpressions;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class PackagePinAgreementTests
{
    // One PackageReference element, whatever its attributes and their order.
    private static readonly Regex PackageReference = new("<PackageReference\\b(?<attributes>[^>]*)>", RegexOptions.CultureInvariant);

    private static readonly Regex Attribute = new(
        "\\b(?<name>Include|Version)\\s*=\\s*\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant);

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

    [Fact]
    public void The_scan_reaches_projects_the_solution_does_not_list()
    {
        var root = RepositoryRoot();
        var scanned = ProjectFiles(root).Select(path => Relative(root, path)).ToList();

        Assert.Contains("eng/fixtures/DeploymentConsumer/DeploymentConsumer.csproj", scanned);
        Assert.Contains("benchmarks/EvolutionNoise/EvolutionNoise.csproj", scanned);
    }

    [Fact]
    public void A_pin_is_read_whatever_the_attribute_order()
    {
        var pins = Pins(
            "<PackageReference Include=\"A\" Version=\"1.0.0\" />" +
            "<PackageReference Version=\"2.0.0\" Include=\"B\" />" +
            "<PackageReference Include=\"C\" PrivateAssets=\"all\" Version=\"3.0.0\">" +
            "<PackageReference Include=\"D\" Version=\"$(DVersion)\" />" +
            "<PackageReference Include=\"E\" />").ToList();

        Assert.Equal(new[] { ("A", "1.0.0"), ("B", "2.0.0"), ("C", "3.0.0") }, pins);
    }

    // The literal (Include, Version) pins of a project file. A version taken from an MSBuild property, or none, is not
    // a pin this test can compare.
    private static IEnumerable<(string Id, string Version)> Pins(string projectText) =>
        PackageReference.Matches(projectText).Cast<Match>()
            .Select(element => Attribute.Matches(element.Groups["attributes"].Value).Cast<Match>()
                .ToDictionary(a => a.Groups["name"].Value, a => a.Groups["value"].Value, StringComparer.Ordinal))
            .Where(a => a.TryGetValue("Include", out var id) && id.Length > 0
                && a.TryGetValue("Version", out var version) && version.Length > 0 && version.IndexOf('$') < 0)
            .Select(a => (a["Include"], a["Version"]));

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
