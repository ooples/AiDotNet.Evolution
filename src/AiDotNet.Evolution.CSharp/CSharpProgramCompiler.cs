using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AiDotNet.Evolution.Programs;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDotNet.Evolution.CSharp;

/// <summary>Immutable reference bundle, multi-file method-body patches, deterministic emit; never executes code.</summary>
/// <remarks>Adapted from the AiDotNet US-03 single-file compiler. Compilation cancellation is cooperative,
/// not an OS memory/CPU/security boundary. Construct inside the improvement loop's charged factory.</remarks>
public sealed class CSharpProgramCompiler : IProgramCompiler
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12, DocumentationMode.None);
    private readonly MetadataReference[] _references;
    private readonly TimeSpan _timeout;
    public string Fingerprint { get; }

    public CSharpProgramCompiler(IEnumerable<string> referencePaths, string targetIdentity, int timeoutSeconds = 10)
    {
        ArgumentNullException.ThrowIfNull(referencePaths);
        if (string.IsNullOrWhiteSpace(targetIdentity) || targetIdentity.Length > 256 || timeoutSeconds is < 1 or > 30)
            throw new ArgumentException("A pinned target identity and bounded timeout are required.");
        _timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var references = new SortedDictionary<string, MetadataReference>(StringComparer.Ordinal);
        long bytesRead = 0;
        foreach (string path in referencePaths)
        {
            if (references.Count == 64) throw new ArgumentException("At most 64 reference images are supported.");
            using var input = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > 32 * 1024 * 1024 || (bytesRead += input.Length) > 128 * 1024 * 1024)
                throw new ArgumentException("Reference images exceed their byte bounds.");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new IOException("A reference changed while being read.");
            var reference = MetadataReference.CreateFromImage(ImmutableArray.CreateRange(bytes));
            using var metadata = reference.GetMetadata();
            if (metadata is not AssemblyMetadata assembly || assembly.GetModules().Length != 1 ||
                !assembly.GetModules()[0].GetMetadataReader().IsAssembly)
                throw new ArgumentException("References must be single managed assembly images.");
            if (!references.TryAdd(ProgramSnapshot.Digest(bytes), reference)) throw new ArgumentException("Duplicate reference content.");
        }
        if (references.Count == 0) throw new ArgumentException("Reference images are required.");
        _references = references.Values.ToArray();
        Fingerprint = ProgramSnapshot.Digest(JsonSerializer.Serialize(new
        {
            Protocol = "evolution-csharp-multifile-v1",
            Target = targetIdentity,
            Language = "CSharp12",
            Options = "library-release-safe-deterministic",
            TimeoutSeconds = timeoutSeconds,
            Compiler = typeof(CSharpCompilation).Assembly.ManifestModule.ModuleVersionId,
            Common = typeof(Compilation).Assembly.ManifestModule.ModuleVersionId,
            Adapter = typeof(CSharpProgramCompiler).Assembly.ManifestModule.ModuleVersionId,
            References = references.Keys.ToArray()
        }));
    }

    public IReadOnlyList<EditTarget> Catalog(ProgramSnapshot source, CancellationToken cancellationToken = default)
    {
        using var timeout = Timeout(cancellationToken);
        var targets = new List<EditTarget>();
        foreach (var file in source.Files)
        {
            var tree = Tree(file.Key, file.Value, timeout.Token);
            if (tree.GetDiagnostics(timeout.Token).Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new ArgumentException("The parent must have valid syntax.");
            foreach (var node in tree.GetRoot(timeout.Token).DescendantNodes())
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (node is not StatementSyntax && node is not ExpressionSyntax) continue;
                var method = node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
                if (method is null || !(method.Body?.Span.Contains(node.Span) == true ||
                    method.ExpressionBody?.Expression.Span.Contains(node.Span) == true)) continue;
                if (targets.Count == 1024) throw new ArgumentException("The syntax catalog exceeds 1024 nodes.");
                targets.Add(new(file.Key, node.SpanStart, node.Span.Length, node.Kind().ToString(),
                    ProgramSnapshot.Digest(file.Value.Substring(node.SpanStart, node.Span.Length))));
            }
        }
        if (targets.Count == 0) throw new ArgumentException("No method-body syntax targets exist.");
        return targets.AsReadOnly();
    }

    public ProgramSnapshot Apply(ProgramSnapshot parent, PatchPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ParentFingerprint != parent.Fingerprint || string.IsNullOrWhiteSpace(plan.Hypothesis) ||
            plan.Hypothesis.Length > 1024 || plan.Edits is null || plan.Edits.Count is < 1 or > 16)
            throw new ArgumentException("The plan does not identify a bounded original snapshot.");
        using var timeout = Timeout(cancellationToken);
        var catalog = Catalog(parent, timeout.Token).ToHashSet();
        var changes = new Dictionary<string, List<TextChange>>(StringComparer.Ordinal);
        foreach (var edit in plan.Edits)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (edit is null || edit.Target is null || !catalog.Contains(edit.Target) ||
                string.IsNullOrEmpty(edit.Replacement) || edit.Replacement.Length > ProgramSnapshot.MaximumCharacters)
                throw new ArgumentException("The edit does not match an original syntax target.");
            var target = edit.Target;
            // Reuse the exact parent node's syntactic category, not a model-supplied category flag.
            var original = Tree(target.File, parent.Files[target.File], timeout.Token).GetRoot(timeout.Token)
                .FindNode(new TextSpan(target.Start, target.Length), getInnermostNodeForTie: true);
            SyntaxNode replacement = original is StatementSyntax
                ? SyntaxFactory.ParseStatement(edit.Replacement, options: ParseOptions, consumeFullText: true)
                : SyntaxFactory.ParseExpression(edit.Replacement, options: ParseOptions, consumeFullText: true);
            if (replacement.ContainsDiagnostics || replacement.ContainsSkippedText || replacement.ContainsDirectives ||
                replacement.FullSpan.Length != edit.Replacement.Length)
                throw new ArgumentException("Replace one complete statement or expression without directives.");
            if (!changes.TryGetValue(target.File, out var list)) changes.Add(target.File, list = new());
            list.Add(new TextChange(new TextSpan(target.Start, target.Length), edit.Replacement));
        }
        var files = new Dictionary<string, string>(parent.Files, StringComparer.Ordinal);
        long total = parent.Files.Values.Sum(s => (long)s.Length);
        foreach (var change in changes)
        {
            var ordered = change.Value.OrderBy(c => c.Span.Start).ToArray();
            for (int i = 1; i < ordered.Length; i++)
                if (ordered[i - 1].Span.End > ordered[i].Span.Start) throw new ArgumentException("Edits overlap.");
            total += ordered.Sum(c => (long)c.NewText!.Length - c.Span.Length);
            if (total > ProgramSnapshot.MaximumCharacters) throw new ArgumentException("The candidate exceeds its source bound.");
            files[change.Key] = SourceText.From(files[change.Key], Encoding.UTF8).WithChanges(ordered).ToString();
        }
        var candidate = new ProgramSnapshot(files);
        if (candidate.Fingerprint == parent.Fingerprint) throw new ArgumentException("The patch is unchanged.");
        return candidate;
    }

    public ProgramBuild Build(ProgramSnapshot source, CancellationToken cancellationToken = default)
    {
        using var timeout = Timeout(cancellationToken);
        var trees = source.Files.Select(f => Tree(f.Key, f.Value, timeout.Token)).ToArray();
        var compilation = CSharpCompilation.Create("EvolutionCandidate", trees, _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false, deterministic: true, concurrentBuild: false));
        using var image = new BoundedImageStream();
        var emitted = compilation.Emit(image, cancellationToken: timeout.Token);
        if (!emitted.Success)
        {
            // No source snippets, mapped paths, exception messages or #line payloads in model feedback.
            string feedback = string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(8).Select(d => $"{d.Id}@{d.Location.SourceSpan.Start}:{d.Location.SourceSpan.Length}"));
            return new(null, feedback);
        }
        // Stronger than public-API equality: preserve all non-method-body syntax, including private signatures,
        // attributes, using aliases, field initializers and directives. No candidate code is loaded for this check.
        var contracts = trees.Select(tree =>
        {
            var bodies = tree.GetRoot(timeout.Token).DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
                .SelectMany(method => method.Body is { } body
                    ? new[] { new TextChange(body.Span, "{}") }
                    : method.ExpressionBody is { } expression
                        ? new[] { new TextChange(expression.Expression.Span, "0") } : Array.Empty<TextChange>());
            return new { tree.FilePath, Contract = tree.GetText(timeout.Token).WithChanges(bodies).ToString() };
        }).ToArray();
        string api = ProgramSnapshot.Digest(JsonSerializer.Serialize(contracts));
        return new(new ProgramArtifact(source, Fingerprint, api, image.ToArray()), "");
    }

    private static SyntaxTree Tree(string path, string text, CancellationToken token) =>
        CSharpSyntaxTree.ParseText(text, ParseOptions, path, Encoding.UTF8, token);
    private CancellationTokenSource Timeout(CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(_timeout);
        return source;
    }
    private sealed class BoundedImageStream : MemoryStream
    {
        private static void Check(long end)
        {
            if (end > 8 * 1024 * 1024) throw new InvalidDataException("The emitted image exceeds 8 MiB.");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(Position + count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(Position + buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(Position + 1); base.WriteByte(value); }
        public override void SetLength(long value) { Check(value); base.SetLength(value); }
    }
}
