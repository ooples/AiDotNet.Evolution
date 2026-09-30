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
    public void Every_stable_type_links_a_test_that_uses_it_and_a_guide_that_names_it()
    {
        // The evidence is the table's own links, reviewed with the table, rather than any mention anywhere: a type named
        // only in a comment, or only in some unrelated file, is not evidence that it is tested or documented.
        string docs = Path.Combine(Root, "docs");
        string testsRoot = Path.Combine(Root, "tests") + Path.DirectorySeparatorChar;
        var failures = new List<string>();
        int stable = 0;
        foreach (string line in File.ReadAllLines(Path.Combine(docs, "API-CLASSIFICATION.md")))
        {
            Match row = Row.Match(line);
            if (!row.Success || row.Groups["id"].Success) continue;
            stable++;
            string type = row.Groups["type"].Value.Replace("&lt;", "<").Replace("&gt;", ">");
            Regex word = Word(ShortName(type));
            bool tested = false, guided = false;
            foreach (Match link in Link.Matches(line))
            {
                string target = Path.GetFullPath(Path.Combine(docs, link.Groups["path"].Value));
                if (!File.Exists(target))
                {
                    failures.Add(type + ": linked evidence " + link.Groups["path"].Value + " does not exist");
                    continue;
                }

                bool code = target.EndsWith(".cs", StringComparison.Ordinal);
                string text = code ? CodeOnly(File.ReadAllText(target)) : File.ReadAllText(target);
                if (code && target.StartsWith(testsRoot, StringComparison.Ordinal) &&
                    !target.EndsWith(nameof(ApiClassificationTests) + ".cs", StringComparison.Ordinal)) tested |= word.IsMatch(text);
                else guided |= word.IsMatch(text);
            }

            if (!tested) failures.Add(type + ": no linked test uses it in code");
            if (!guided) failures.Add(type + ": no linked guide or example names it");
        }

        Assert.True(stable > 300, $"only {stable} stable rows were read; the table is not being parsed");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Code_evidence_ignores_comments_and_longer_names()
    {
        // Negative controls for the rule above: a mention in a comment or a string, and a longer type sharing the prefix,
        // are not uses, while a string containing // does not hide the code after it.
        Regex word = Word("EvolutionWorkServer");
        Assert.DoesNotMatch(word, CodeOnly("// EvolutionWorkServer is not used here\n/* EvolutionWorkServer */ int x = 1;"));
        Assert.DoesNotMatch(word, CodeOnly("var options = new EvolutionWorkServerOptions();"));
        Assert.DoesNotMatch(word, CodeOnly("Assert.Equal(\"EvolutionWorkServer\", name);"));
        Assert.Matches(word, CodeOnly("string url = \"http://host\"; using var server = new EvolutionWorkServer(c, cert, token);"));
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
        // Shipped declarations are public too: a type moved there on release must stay classified.
        foreach (string api in Directory.EnumerateFiles(Path.Combine(Root, "src"), "PublicAPI.*.txt", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(path) is "PublicAPI.Shipped.txt" or "PublicAPI.Unshipped.txt"))
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

    private static readonly Regex Link = new(@"\[[^\]]+\]\((?<path>[^)]+)\)", RegexOptions.None, RegexTimeout);

    // Comments and the contents of string and character literals are removed: a name written in either is not a use.
    // Literals are matched first, so a "//" inside one does not hide the code after it.
    private static readonly Regex CommentOrLiteral = new(
        @"//[^\n]*|/\*.*?\*/|@""(?:""""|[^""])*""|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])'",
        RegexOptions.Singleline, RegexTimeout);

    private static string CodeOnly(string source) =>
        CommentOrLiteral.Replace(source, match => match.Value.StartsWith("/", StringComparison.Ordinal) ? " " : "\"\"");

    private static string ShortName(string type) =>
        Regex.Replace(type.Substring(type.LastIndexOf('.') + 1), "[<`].*", string.Empty, RegexOptions.None, RegexTimeout);

    private static Regex Word(string name) => new(@"\b" + Regex.Escape(name) + @"\b", RegexOptions.None, RegexTimeout);
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
