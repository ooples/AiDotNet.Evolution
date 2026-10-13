using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Ptx;

/// <summary>The static facts about a candidate that need no GPU: header, launch override, entry signature, body lines.</summary>
internal sealed record PtxSourceInspection(IReadOnlyList<CompilationDiagnostic> Diagnostics, PtxLaunchConfiguration Launch,
    int BodyFirstLine, int BodyLastLine)
{
    internal bool HasErrors => Diagnostics.Any(d => d.Severity == CompilationDiagnosticSeverity.Error);
}

/// <summary>Checks a candidate's PTX against its contract before any driver sees it.</summary>
/// <remarks>The launch header is an ordinary PTX comment on the first line, <c>// aidotnet-launch: block=256,1,1</c>,
/// so a candidate stays loadable PTX while carrying the one launch change a rewrite may make.</remarks>
internal static class PtxSourceInspector
{
    internal const string Tool = "aidotnet-ptx";
    internal const string LaunchHeaderPrefix = "// aidotnet-launch: block=";
    internal const int MaximumSourceCharacters = 262_144;
    private static readonly Regex Header = new(@"^// aidotnet-launch: block=(\d{1,4}),(\d{1,4}),(\d{1,2})[ \t]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Version = new(@"(^|\s)\.version\s+(\d+)\.(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Target = new(@"(^|\s)\.target\s+sm_(\d+)([a-z]?)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex AddressSize = new(@"(^|\s)\.address_size\s+(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Entry = new(@"\.entry\s+([A-Za-z_$%][\w$]*)\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly string[] ParameterTypes =
        { ".u8", ".s8", ".b8", ".u16", ".s16", ".b16", ".f16", ".u32", ".s32", ".b32", ".f32", ".u64", ".s64", ".b64", ".f64" };

    internal static string ScalarParamType(PtxElementType type) => type switch
    {
        PtxElementType.Float32 => ".f32",
        PtxElementType.Float64 => ".f64",
        PtxElementType.Int32 => ".s32",
        PtxElementType.UInt32 => ".u32",
        PtxElementType.Int64 => ".s64",
        _ => ".b" + (type.Size() * 8).ToString(CultureInfo.InvariantCulture)
    };

    internal static string FormatLaunchHeader(int x, int y, int z) =>
        LaunchHeaderPrefix + x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + "," + z.ToString(CultureInfo.InvariantCulture);

    /// <summary>Replaces or inserts the launch header.</summary>
    internal static string WithLaunchHeader(string source, int x, int y, int z)
    {
        string header = FormatLaunchHeader(x, y, z);
        string[] lines = source.Split('\n');
        if (lines.Length > 0 && lines[0].StartsWith(LaunchHeaderPrefix, StringComparison.Ordinal))
            return header + source.Substring(lines[0].Length);
        return header + "\n" + source;
    }

    internal static PtxSourceInspection Inspect(string source, PtxKernelContract contract)
    {
        var diagnostics = new List<CompilationDiagnostic>();
        void Report(string code, string message, int? line = null, CompilationDiagnosticSeverity severity = CompilationDiagnosticSeverity.Error) =>
            diagnostics.Add(new CompilationDiagnostic { Code = code, Message = message, Line = line, Severity = severity, Tool = Tool, FilePath = PtxProgramCompiler.FileName });
        PtxLaunchConfiguration launch = contract.Launch;
        if (string.IsNullOrWhiteSpace(source) || source.Length > MaximumSourceCharacters)
        {
            Report("PTX001", "The source is empty or exceeds " + MaximumSourceCharacters + " characters.");
            return new(diagnostics, launch, 0, -1);
        }
        int nonAscii = source.IndexOf(source.FirstOrDefault(c => c > 126 || (c < 32 && c is not ('\n' or '\r' or '\t'))));
        if (source.Any(c => c > 126 || (c < 32 && c is not ('\n' or '\r' or '\t'))))
            Report("PTX001", "PTX must be printable ASCII.", LineOf(source, Math.Max(0, nonAscii)));
        string[] rawLines = source.Split('\n');
        if (rawLines[0].StartsWith(LaunchHeaderPrefix, StringComparison.Ordinal))
        {
            Match header = Header.Match(rawLines[0].TrimEnd('\r'));
            if (!header.Success) Report("PTX008", "The launch header must read '" + LaunchHeaderPrefix + "X,Y,Z'.", 1);
            else
            {
                int x = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture), y = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture),
                    z = int.Parse(header.Groups[3].Value, CultureInfo.InvariantCulture);
                if (x < 1 || y < 1 || z < 1 || (long)x * y * z > contract.Target.MaxThreadsPerBlock || z > 64)
                    Report("PTX008", "The launch header's block " + x + "x" + y + "x" + z + " exceeds the target's " + contract.Target.MaxThreadsPerBlock + " threads.", 1);
                else launch = contract.Launch.WithBlock(x, y, z);
            }
        }
        string code = StripComments(source);
        Match version = Version.Match(code);
        if (!version.Success) Report("PTX002", "A '.version' directive is required.");
        Match target = Target.Match(code);
        if (!target.Success) Report("PTX003", "A '.target sm_XX' directive is required.");
        else if (int.Parse(target.Groups[2].Value, CultureInfo.InvariantCulture) > contract.Target.SmVersion)
            Report("PTX003", "'.target sm_" + target.Groups[2].Value + "' exceeds the contract target " + contract.Target.TargetName + ".", LineOf(code, target.Index));
        Match address = AddressSize.Match(code);
        if (!address.Success || address.Groups[2].Value != "64") Report("PTX004", "'.address_size 64' is required.", address.Success ? LineOf(code, address.Index) : null);
        Match[] entries = Entry.Matches(code).Where(m => m.Groups[1].Value == contract.EntryPoint).ToArray();
        if (entries.Length != 1)
        {
            Report("PTX005", entries.Length == 0 ? "The entry '.entry " + contract.EntryPoint + "' is missing." : "The entry '" + contract.EntryPoint + "' is defined more than once.");
            return new(diagnostics, launch, 0, -1);
        }
        int open = entries[0].Index + entries[0].Length - 1, close = code.IndexOf(')', open);
        if (close < 0)
        {
            Report("PTX006", "The entry's parameter list is not closed.", LineOf(code, open));
            return new(diagnostics, launch, 0, -1);
        }
        string[] parameters = code.Substring(open + 1, close - open - 1).Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        if (parameters.Length != contract.Parameters.Count)
            Report("PTX006", "The entry declares " + parameters.Length + " parameters; the contract requires " + contract.Parameters.Count + ".", LineOf(code, open));
        for (int i = 0; i < Math.Min(parameters.Length, contract.Parameters.Count); i++)
        {
            string[] tokens = parameters[i].Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string? type = tokens.FirstOrDefault(t => ParameterTypes.Contains(t, StringComparer.Ordinal));
            PtxKernelParameter expected = contract.Parameters[i];
            int bytes = expected.Kind == PtxParameterKind.Buffer ? 8 : expected.ElementType.Size();
            if (tokens.Length == 0 || tokens[0] != ".param" || type is null || tokens.Any(t => t.Contains('[', StringComparison.Ordinal)))
                Report("PTX007", "Parameter " + i + " ('" + expected.Name + "') must be a scalar '.param' of " + bytes + " bytes.", LineOf(code, open));
            else if (int.Parse(type.Substring(2), CultureInfo.InvariantCulture) != bytes * 8)
                Report("PTX007", "Parameter " + i + " ('" + expected.Name + "') is " + type + "; the contract passes " + bytes + " bytes.", LineOf(code, open));
            else if (expected.Kind == PtxParameterKind.Scalar && type != ScalarParamType(expected.ElementType) && type[1] != 'b')
                Report("PTX007", "Parameter " + i + " ('" + expected.Name + "') is " + type + " but carries " + expected.ElementType + ".", LineOf(code, open), CompilationDiagnosticSeverity.Warning);
        }
        int bodyOpen = code.IndexOf('{', close), depth = 0, bodyClose = -1;
        for (int i = Math.Max(0, bodyOpen); bodyOpen >= 0 && i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0)
            {
                bodyClose = i;
                break;
            }
        }
        if (bodyOpen < 0 || bodyClose < 0)
        {
            Report("PTX009", "The entry has no complete body.", LineOf(code, close));
            return new(diagnostics, launch, 0, -1);
        }
        try
        {
            foreach (IReadOnlyDictionary<string, long> shape in contract.ValidationShapes.Append(contract.TimingShape)) _ = launch.Resolve(shape);
        }
        catch (ArgumentException exception)
        {
            Report("PTX008", "The launch cannot express a contract shape: " + exception.Message, 1);
        }
        return new(diagnostics, launch, LineOf(code, bodyOpen), LineOf(code, bodyClose) - 2);
    }

    /// <summary>Replaces comments with spaces, preserving every newline and offset.</summary>
    internal static string StripComments(string source)
    {
        var text = new StringBuilder(source);
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '/' && text[i + 1] == '/')
                for (; i < text.Length && text[i] != '\n'; i++) text[i] = ' ';
            else if (text[i] == '/' && text[i + 1] == '*')
            {
                for (; i < text.Length - 1 && !(text[i] == '*' && text[i + 1] == '/'); i++)
                    if (text[i] != '\n') text[i] = ' ';
                if (i < text.Length - 1)
                {
                    text[i] = ' ';
                    text[i + 1] = ' ';
                }
            }
        }
        return text.ToString();
    }

    /// <summary>One-based line of a character offset.</summary>
    internal static int LineOf(string text, int offset)
    {
        int line = 1;
        for (int i = 0; i < offset && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}