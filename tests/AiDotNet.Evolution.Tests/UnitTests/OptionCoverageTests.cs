using System.Text.RegularExpressions;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-83 (#185), defect class D8: OpenEvolve 0.3.2 declares options that do nothing. Every public option here must be
/// exercised by at least one test: set or read in test code, where a test can observe what it does.
/// </summary>
public sealed class OptionCoverageTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void D8_every_public_option_is_exercised_by_a_test()
    {
        string root = RepositoryRoot();
        var property = new Regex(@"^\s+public [\w<>?\[\], .]+ (\w+) \{ get; (?:set|init); \}", RegexOptions.Multiline, RegexTimeout);
        string tests = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(path => !path.EndsWith(nameof(OptionCoverageTests) + ".cs", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        var options = new List<string>();
        var unexercised = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*Options.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            string type = Path.GetFileNameWithoutExtension(file);
            foreach (Match match in property.Matches(File.ReadAllText(file)))
            {
                string name = match.Groups[1].Value;
                options.Add(type + "." + name);
                // Set in an initializer or assignment, or read as a member.
                bool used = Regex.IsMatch(tests, @"\b" + name + @"\s*=[^=>]", RegexOptions.None, RegexTimeout) ||
                            Regex.IsMatch(tests, @"\." + name + @"\b", RegexOptions.None, RegexTimeout);
                if (!used) unexercised.Add(type + "." + name);
            }
        }

        Assert.True(options.Count > 200, $"only {options.Count} options were found; the scan is not reading the sources");
        Assert.True(unexercised.Count == 0, "Options no test exercises: " + string.Join(", ", unexercised));
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src")) && Directory.Exists(Path.Combine(directory.FullName, "tests")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
