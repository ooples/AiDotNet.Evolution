using System.Text.RegularExpressions;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-82 (#184): every public type is classified in docs/API-CLASSIFICATION.md, experimental types carry their
/// diagnostic, and every stable type has documentation (compiler-enforced), a test and a guide or example.
/// Regenerate the table with eng/generate-api-classification.py.
/// </summary>
public sealed class ApiClassificationTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex TypeLine = new(
        @"^(?:\[(?<id>\w+)\])?(?:(?:static|abstract|sealed|virtual|readonly|override)\s+)*(?<type>[A-Za-z0-9_.`<>,]+)$",
        RegexOptions.None, RegexTimeout);
    private static readonly Regex Row = new(@"^\| `(?<type>[^`]+)` \| (?<class>Stable|Experimental \((?<id>\w+)\)) \|",
        RegexOptions.None, RegexTimeout);

    private static readonly string[] NotGuides =
        { "API-CLASSIFICATION.md", "COMPETITIVE_ANALYSIS_AND_ROADMAP.md", "IMPLEMENTATION_STATUS.md", "USER_STORY_DELIVERY.md" };

    [Fact]
    public void Every_public_type_is_classified_exactly_once_as_its_attribute_says()
    {
        Dictionary<string, (string Package, string? Id)> declared = DeclaredTypes();
        Assert.True(declared.Count > 400, $"only {declared.Count} public types were read; the scan is not reading the API files");

        var classified = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(Root, "docs", "API-CLASSIFICATION.md")))
        {
            Match match = Row.Match(line);
            if (!match.Success) continue;
            string type = match.Groups["type"].Value.Replace("&lt;", "<").Replace("&gt;", ">");
            Assert.False(classified.ContainsKey(type), type + " is listed twice");
            classified[type] = match.Groups["id"].Success ? match.Groups["id"].Value : null;
        }

        Assert.Empty(declared.Keys.Except(classified.Keys).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Empty(classified.Keys.Except(declared.Keys).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Empty(declared.Where(pair => pair.Value.Id != classified[pair.Key]).Select(pair => pair.Key));
    }

    [Fact]
    public void Every_stable_type_has_a_test_and_a_guide()
    {
        string tests = Text(Directory.EnumerateFiles(Path.Combine(Root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(nameof(ApiClassificationTests) + ".cs", StringComparison.Ordinal)));
        string guides = Text(Directory.EnumerateFiles(Path.Combine(Root, "docs"), "*.md", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "docs", "migration"), "*.md", SearchOption.AllDirectories))
            .Where(path => !NotGuides.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "examples"), "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".md", StringComparison.Ordinal))));

        var untested = new List<string>();
        var unguided = new List<string>();
        foreach (KeyValuePair<string, (string Package, string? Id)> declared in DeclaredTypes())
        {
            string type = declared.Key;
            if (declared.Value.Id is not null) continue;
            string name = Regex.Replace(type.Substring(type.LastIndexOf('.') + 1), "[<`].*", string.Empty, RegexOptions.None, RegexTimeout);
            var word = new Regex(@"\b" + Regex.Escape(name) + @"\b", RegexOptions.None, RegexTimeout);
            if (!word.IsMatch(tests)) untested.Add(type);
            if (!word.IsMatch(guides)) unguided.Add(type);
        }

        Assert.True(untested.Count == 0, "Stable types no test names: " + string.Join(", ", untested));
        Assert.True(unguided.Count == 0, "Stable types no guide or example names: " + string.Join(", ", unguided));
    }

    [Fact]
    public void Every_packaged_assembly_enforces_xml_documentation()
    {
        foreach (string project in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(project);
            if (text.IndexOf("<EnablePackageValidation>true</EnablePackageValidation>", StringComparison.Ordinal) < 0) continue;
            Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", text, StringComparison.Ordinal);
            Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", text, StringComparison.Ordinal);
            Assert.Contains("<PackageValidationBaselineVersion>", text, StringComparison.Ordinal);
        }
    }

    private static Dictionary<string, (string Package, string? Id)> DeclaredTypes()
    {
        var types = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (string api in Directory.EnumerateFiles(Path.Combine(Root, "src"), "PublicAPI.Unshipped.txt", SearchOption.AllDirectories))
        {
            string package = Path.GetFileName(Path.GetDirectoryName(api)) ?? string.Empty;
            foreach (string raw in File.ReadAllLines(api))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line.IndexOf("->", StringComparison.Ordinal) >= 0 || line.IndexOf('(') >= 0) continue;
                Match match = TypeLine.Match(line);
                if (!match.Success) continue;
                string type = match.Groups["type"].Value;
                string name = type.Substring(type.LastIndexOf('.') + 1);
                if (name.Length == 0 || char.IsLower(name[0])) continue;
                types[type] = (package, match.Groups["id"].Success ? match.Groups["id"].Value : null);
            }
        }

        return types;
    }

    private static string Text(IEnumerable<string> paths) =>
        string.Join("\n", paths.Where(path => path.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) < 0
                                              && path.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) < 0)
            .Select(File.ReadAllText));

    private static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AiDotNet.Evolution.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
