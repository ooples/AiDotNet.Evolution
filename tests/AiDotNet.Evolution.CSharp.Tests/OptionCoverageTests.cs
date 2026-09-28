#if NET
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

/// <summary>
/// V1-83 (#185), defect class D8: OpenEvolve 0.3.2 declares options that do nothing. Every public option here must be
/// exercised by at least one test: set or read in test code, where a test can observe what it does.
/// </summary>
/// <remarks>
/// References are resolved by symbol, not by name, so a test that sets <c>EvolutionTraceOptions.Enabled</c> does not
/// count for a different options type's <c>Enabled</c>. The sources of every package and every test project are
/// compiled together, so no test project's build output is needed and each project's tests can reach any option.
/// </remarks>
public sealed class OptionCoverageTests
{
    [Fact]
    public void D8_every_public_option_is_exercised_by_a_test()
    {
        string root = RepositoryRoot();
        CSharpParseOptions parse = new CSharpParseOptions(LanguageVersion.Preview)
            .WithPreprocessorSymbols("NET", "NET10_0", "NET10_0_OR_GREATER", "NET8_0_OR_GREATER", "NET6_0_OR_GREATER", "NETCOREAPP");
        List<SyntaxTree> sources = Parse(Path.Combine(root, "src"), parse);
        List<SyntaxTree> tests = Parse(Path.Combine(root, "tests"), parse)
            .Where(tree => !tree.FilePath.EndsWith(nameof(OptionCoverageTests) + ".cs", StringComparison.Ordinal))
            .ToList();
        SyntaxTree implicitUsings = CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq;" +
            " global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;", parse);

        // Framework and third-party metadata; this repository's own assemblies are compiled from source instead.
        IEnumerable<MetadataReference> references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            .Where(path => !Path.GetFileName(path).StartsWith("AiDotNet.Evolution", StringComparison.Ordinal))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => MetadataReference.CreateFromFile(group.First()));
        CSharpCompilation compilation = CSharpCompilation.Create("option-coverage", sources.Concat(tests).Append(implicitUsings),
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var options = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (SyntaxTree tree in sources.Where(tree => Path.GetFileName(tree.FilePath).EndsWith("Options.cs", StringComparison.Ordinal)))
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (PropertyDeclarationSyntax declaration in tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is IPropertySymbol property && IsPublicOption(property)) options.Add(property);
            }
        }

        var names = new HashSet<string>(options.Select(option => option.Name), StringComparer.Ordinal);
        var exercised = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (SyntaxTree tree in tests)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (IdentifierNameSyntax identifier in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!names.Contains(identifier.Identifier.ValueText)) continue;
                SymbolInfo info = model.GetSymbolInfo(identifier);
                // An unresolved or ambiguous reference counts for nothing, so a binding failure shows up as a gap.
                if (info.Symbol is IPropertySymbol property) exercised.Add(property.OriginalDefinition);
            }
        }

        List<string> unexercised = options.Where(option => !exercised.Contains(option.OriginalDefinition))
            .Select(option => option.ContainingType.ToDisplayString() + "." + option.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.True(options.Count > 200, $"only {options.Count} options were found; the scan is not reading the sources");
        // Control: a property every engine test sets resolves to its declaring type, so binding works at all.
        Assert.Contains(exercised, property => property.Name == "Seed" && property.ContainingType.Name == "EvolutionEngineOptions");
        Assert.True(unexercised.Count == 0, "Options no test exercises: " + string.Join(", ", unexercised));
    }

    private static bool IsPublicOption(IPropertySymbol property)
    {
        if (property.DeclaredAccessibility != Accessibility.Public || property.IsStatic || property.IsIndexer) return false;
        for (INamedTypeSymbol? type = property.ContainingType; type is not null; type = type.ContainingType)
            if (type.DeclaredAccessibility != Accessibility.Public) return false;
        return property.GetMethod is { DeclaredAccessibility: Accessibility.Public } &&
               property.SetMethod is { DeclaredAccessibility: Accessibility.Public };
    }

    private static List<SyntaxTree> Parse(string directory, CSharpParseOptions parse)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(separator + "obj" + separator, StringComparison.Ordinal) &&
                           !path.Contains(separator + "bin" + separator, StringComparison.Ordinal))
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), parse, path))
            .ToList();
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src")) && Directory.Exists(Path.Combine(directory.FullName, "tests")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
#endif