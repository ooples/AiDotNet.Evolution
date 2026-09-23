using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution.Programs;

/// <summary>
/// An <see cref="IProgramCompiler"/> for Python sources, so the compiler-guided improvement loop (US-17) runs
/// for Python tasks too. A trusted helper runs in an isolated interpreter (<c>-I -S -B</c>) and only parses
/// (<c>ast</c>) and compiles (<c>compile</c>); candidate code is never executed here. Offsets are UTF-16 code
/// units, matching .NET strings, and build feedback carries positions and messages but no source text.
/// </summary>
public sealed class PythonProgramCompiler : IProgramCompiler
{
    private const int MaximumTargets = 1024;
    private readonly string _python;
    private readonly TimeSpan _timeout;
    private string? _fingerprint;

    /// <summary>Creates a compiler that uses the given Python 3.8+ interpreter.</summary>
    /// <param name="pythonExecutable">Path to the interpreter.</param>
    /// <param name="timeout">Per-operation limit; the default is 30 seconds.</param>
    public PythonProgramCompiler(string pythonExecutable, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutable);
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(30);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _python = pythonExecutable;
        _timeout = limit;
    }

    /// <inheritdoc/>
    public IReadOnlyList<EditTarget> Catalog(ProgramSnapshot source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using JsonDocument reply = Invoke("catalog", new { files = source.Files }, cancellationToken);
        if (reply.RootElement.TryGetProperty("error", out JsonElement error))
            throw new ArgumentException("The parent must have valid syntax: " + error.GetString());
        var targets = new List<EditTarget>();
        foreach (JsonElement item in reply.RootElement.GetProperty("targets").EnumerateArray())
        {
            if (targets.Count == MaximumTargets) throw new ArgumentException("The syntax catalog exceeds 1024 nodes.");
            string file = item.GetProperty("file").GetString()!;
            int start = item.GetProperty("start").GetInt32(), length = item.GetProperty("length").GetInt32();
            string text = source.Files[file];
            if (start < 0 || length < 1 || start + length > text.Length) throw new InvalidDataException("The helper returned an invalid span.");
            targets.Add(new EditTarget(file, start, length, item.GetProperty("kind").GetString()!,
                ProgramSnapshot.Digest(text.Substring(start, length))));
        }
        return targets;
    }

    /// <inheritdoc/>
    public ProgramSnapshot Apply(ProgramSnapshot parent, PatchPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ParentFingerprint != parent.Fingerprint || string.IsNullOrWhiteSpace(plan.Hypothesis) ||
            plan.Hypothesis.Length > 1024 || plan.Edits is null || plan.Edits.Count is < 1 or > 16)
            throw new ArgumentException("The plan does not identify a bounded original snapshot.");
        var catalog = Catalog(parent, cancellationToken).ToHashSet();
        var changes = new Dictionary<string, List<(int Start, int Length, string Text)>>(StringComparer.Ordinal);
        foreach (SourceEdit edit in plan.Edits)
        {
            if (edit is null || edit.Target is null || !catalog.Contains(edit.Target) ||
                string.IsNullOrEmpty(edit.Replacement) || edit.Replacement.Length > ProgramSnapshot.MaximumCharacters)
                throw new ArgumentException("The edit does not match an original syntax target.");
            // The parent node's category decides what the replacement must be, never a model-supplied flag.
            using JsonDocument check = Invoke("check", new { kind = edit.Target.Kind, replacement = edit.Replacement }, cancellationToken);
            if (!check.RootElement.GetProperty("ok").GetBoolean())
                throw new ArgumentException("Replace one complete statement or expression.");
            if (!changes.TryGetValue(edit.Target.File, out var list)) changes.Add(edit.Target.File, list = new());
            list.Add((edit.Target.Start, edit.Target.Length, edit.Replacement));
        }
        var files = new Dictionary<string, string>(parent.Files, StringComparer.Ordinal);
        long total = parent.Files.Values.Sum(text => (long)text.Length);
        foreach (var change in changes)
        {
            var ordered = change.Value.OrderBy(c => c.Start).ToArray();
            for (int i = 1; i < ordered.Length; i++)
                if (ordered[i - 1].Start + ordered[i - 1].Length > ordered[i].Start) throw new ArgumentException("Edits overlap.");
            total += ordered.Sum(c => (long)c.Text.Length - c.Length);
            if (total > ProgramSnapshot.MaximumCharacters) throw new ArgumentException("The candidate exceeds its source bound.");
            var builder = new StringBuilder(files[change.Key]);
            foreach (var c in ordered.Reverse()) builder.Remove(c.Start, c.Length).Insert(c.Start, c.Text);
            files[change.Key] = builder.ToString();
        }
        var candidate = new ProgramSnapshot(files);
        if (candidate.Fingerprint == parent.Fingerprint) throw new ArgumentException("The patch is unchanged.");
        return candidate;
    }

    /// <inheritdoc/>
    public ProgramBuild Build(ProgramSnapshot source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using JsonDocument reply = Invoke("build", new { files = source.Files }, cancellationToken);
        JsonElement root = reply.RootElement;
        if (!root.GetProperty("ok").GetBoolean()) return new ProgramBuild(null, root.GetProperty("feedback").GetString() ?? "build failed");
        byte[] image = Convert.FromBase64String(root.GetProperty("image").GetString()!);
        string compiler = _fingerprint ??= ProgramSnapshot.Digest("python-compiler-v1|" + root.GetProperty("version").GetString() + "|" +
            ProgramSnapshot.Digest(Helper));
        return new ProgramBuild(new ProgramArtifact(source, compiler, "python-bytecode-v1", image), string.Empty);
    }

    private JsonDocument Invoke(string mode, object payload, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (string argument in new[] { "-I", "-S", "-B", "-c", Helper, mode }) start.ArgumentList.Add(argument);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The Python interpreter did not start.");
        try
        {
            using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                input.Write(JsonSerializer.Serialize(payload));
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
            if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
                throw new TimeoutException("The Python compiler helper exceeded its time limit.");
            cancellationToken.ThrowIfCancellationRequested();
            string text = output.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || text.Length > 16 * 1024 * 1024)
                throw new InvalidOperationException("The Python compiler helper failed: " + errors.GetAwaiter().GetResult().Trim());
            return JsonDocument.Parse(text);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    // Trusted helper: parses and compiles only. Offsets are UTF-16 code units to match .NET strings.
    private const string Helper = """
import ast, base64, json, marshal, sys, textwrap
mode = sys.argv[1]
req = json.loads(sys.stdin.buffer.read().decode("utf-8"))
def u16(text, index):
    return len(text[:index].encode("utf-16-le")) // 2
def offsets(text):
    starts, total = [0], 0
    for line in text.splitlines(keepends=True):
        total += len(line); starts.append(total)
    return starts
def char_at(text, starts, line, col_bytes):
    raw = text[starts[line - 1]:starts[line] if line < len(starts) else len(text)].encode("utf-8")
    return starts[line - 1] + len(raw[:col_bytes].decode("utf-8", "replace"))
def out(value):
    sys.stdout.write(json.dumps(value)); sys.stdout.flush()
if mode == "catalog":
    targets = []
    for name in sorted(req["files"]):
        text = req["files"][name]
        try:
            tree = ast.parse(text, filename=name)
        except SyntaxError as error:
            out({"error": f"{name}:{error.lineno}:{error.offset}: {error.msg}"}); sys.exit(0)
        starts = offsets(text)
        for function in ast.walk(tree):
            if not isinstance(function, (ast.FunctionDef, ast.AsyncFunctionDef)):
                continue
            for node in ast.walk(function):
                if node is function or not isinstance(node, (ast.stmt, ast.expr)) or getattr(node, "end_lineno", None) is None:
                    continue
                if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
                    continue
                s = char_at(text, starts, node.lineno, node.col_offset)
                e = char_at(text, starts, node.end_lineno, node.end_col_offset)
                if e > s:
                    targets.append({"file": name, "start": u16(text, s), "length": u16(text, e) - u16(text, s),
                                    "kind": "statement" if isinstance(node, ast.stmt) else "expression"})
    seen, unique = set(), []
    for t in targets:
        key = (t["file"], t["start"], t["length"], t["kind"])
        if key not in seen:
            seen.add(key); unique.append(t)
    out({"targets": unique})
elif mode == "check":
    rep = req["replacement"]
    try:
        if req["kind"] == "statement":
            body = ast.parse(textwrap.dedent(rep)).body
            ok = len(body) == 1
        else:
            ast.parse(rep.strip(), mode="eval"); ok = rep.strip() == rep
    except SyntaxError:
        ok = False
    out({"ok": ok})
elif mode == "build":
    codes, feedback = {}, []
    for name in sorted(req["files"]):
        try:
            codes[name] = base64.b64encode(marshal.dumps(compile(req["files"][name], name, "exec", dont_inherit=True))).decode()
        except SyntaxError as error:
            feedback.append(f"{name}:{error.lineno}:{error.offset}: {type(error).__name__}: {error.msg}")
        except ValueError as error:
            feedback.append(f"{name}: ValueError: {error}")
    if feedback:
        out({"ok": False, "feedback": "; ".join(feedback)})
    else:
        image = json.dumps(codes, sort_keys=True).encode()
        out({"ok": True, "image": base64.b64encode(image).decode(), "version": sys.version.split()[0]})
""";
}