using System.Globalization;
using System.Text.RegularExpressions;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Ptx;

/// <summary>Turns the driver JIT's ptxas logs into structured diagnostics.</summary>
internal static class PtxJitLog
{
    internal const string Tool = "ptxas";
    private static readonly Regex Line = new(@"^ptxas\s*(?:application ptx input|[^,]*?),?\s*(?:line\s+(\d+);)?\s*(error|warning|info|fatal)\s*:\s*(.*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    internal static List<CompilationDiagnostic> Parse(string log, bool includeInfo)
    {
        var diagnostics = new List<CompilationDiagnostic>();
        foreach (string raw in (log ?? string.Empty).Split('\n'))
        {
            string text = raw.Trim();
            if (text.Length == 0) continue;
            Match match = Line.Match(text);
            if (!match.Success)
            {
                if (diagnostics.Count < 64 && includeInfo)
                    diagnostics.Add(new CompilationDiagnostic { Severity = CompilationDiagnosticSeverity.Info, Message = Bound(text), Tool = Tool });
                continue;
            }
            string kind = match.Groups[2].Value.ToLowerInvariant();
            CompilationDiagnosticSeverity severity = kind switch
            {
                "error" or "fatal" => CompilationDiagnosticSeverity.Error,
                "warning" => CompilationDiagnosticSeverity.Warning,
                _ => CompilationDiagnosticSeverity.Info
            };
            if (severity == CompilationDiagnosticSeverity.Info && !includeInfo) continue;
            if (diagnostics.Count == 64) break;
            diagnostics.Add(new CompilationDiagnostic
            {
                Severity = severity,
                Code = "PTXAS-" + kind.ToUpperInvariant(),
                Message = Bound(match.Groups[3].Value.Trim()),
                Line = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                FilePath = match.Groups[1].Success ? PtxProgramCompiler.FileName : null,
                Tool = Tool
            });
        }
        return diagnostics;
    }

    private static string Bound(string text) => text.Length > 1024 ? text.Substring(0, 1024) : text;
}