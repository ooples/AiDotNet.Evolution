using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AiDotNet.Evolution;

namespace AiDotNet.Evolution.Cli;

/// <summary>aidotnet-evolve: run and resume evolutions; inspect, compare, export and report runs from their trace files.</summary>
public static class Program
{
    private const string Usage = """
        Usage:
          aidotnet-evolve run     <run.json>
          aidotnet-evolve resume  <run.json>
          aidotnet-evolve inspect <trace>
          aidotnet-evolve compare <traceA> <traceB>
          aidotnet-evolve export  <trace> <output-directory>
          aidotnet-evolve report  <trace> <output.html>
        """;

    public static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["run", string runFile] => Evolve(runFile, resume: false),
                ["resume", string runFile] => Evolve(runFile, resume: true),
                ["inspect", string trace] => Print(JsonSerializer.Serialize(TraceAnalysis.Load(trace).Summary(), Json)),
                ["compare", string a, string b] => Print(JsonSerializer.Serialize(TraceAnalysis.Compare(TraceAnalysis.Load(a), TraceAnalysis.Load(b)), Json)),
                ["export", string trace, string output] => Export(trace, output),
                ["report", string trace, string output] => Report(trace, output),
                _ => Fail(Usage)
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or JsonException or
            HttpRequestException or NotSupportedException or InvalidOperationException)
        {
            return Fail("error: " + exception.Message);
        }
    }

    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The first Ctrl+C stops at the next batch boundary and reports results; a second aborts. Either way <c>resume</c> continues.</summary>
    private static int Evolve(string runFile, bool resume)
    {
        using var interrupt = new RunInterrupt();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; interrupt.Press(); };
        Console.CancelKeyPress += handler;
        try { return RunCommand.Execute(runFile, resume, Console.Out, Console.Error, interrupt); }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static int Print(string text) { Console.WriteLine(text); return 0; }
    private static int Fail(string text) { Console.Error.WriteLine(text); return 2; }

    private static int Export(string trace, string output)
    {
        TraceAnalysis analysis = TraceAnalysis.Load(trace);
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "winner.json");
        if (File.Exists(path)) return Fail("error: " + path + " already exists; exports never overwrite.");
        File.WriteAllText(path, JsonSerializer.Serialize(analysis.Winner(), Json));
        return Print(path);
    }

    private static int Report(string trace, string output)
    {
        if (File.Exists(output)) return Fail("error: " + output + " already exists; reports never overwrite.");
        File.WriteAllText(output, HtmlReport.Render(TraceAnalysis.Load(trace)), new UTF8Encoding(false));
        return Print(output);
    }
}

/// <summary>Everything the commands derive from one trace.</summary>
internal sealed class TraceAnalysis
{
    private TraceAnalysis(string path, EvolutionTraceReadResult read, IReadOnlyList<EvolutionTraceRecord> records)
    {
        Path = path; Read = read; Records = records;
    }

    public string Path { get; }
    public EvolutionTraceReadResult Read { get; }
    public IReadOnlyList<EvolutionTraceRecord> Records { get; }

    public static TraceAnalysis Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Trace not found.", path);
        EvolutionTraceReadResult read = EvolutionTraceFile.Read(path);
        var records = EvolutionTraceFile.ReadRecords(path).OrderBy(r => r.Sequence).ToList();
        if (records.Count == 0) throw new InvalidDataException("The trace holds no evaluation records.");
        return new TraceAnalysis(path, read, records);
    }

    private static bool Valid(EvolutionTraceRecord r) => r.Status == EvolutionEvaluationStatus.Completed && r.Quality is not null;
    private bool Better(double a, double b) => Direction == EvolutionOptimizationDirection.Minimize ? a < b : a > b;
    public EvolutionOptimizationDirection Direction => Records[0].Direction;

    public EvolutionTraceRecord? Best => Records.Where(Valid).Aggregate((EvolutionTraceRecord?)null,
        (best, r) => best is null || Better(r.Quality!.Value, best.Quality!.Value) ? r : best);

    /// <summary>Best-so-far quality and cumulative cost after each record, in sequence order.</summary>
    public IReadOnlyList<(long Sequence, double? BestSoFar, double CumulativeCost)> Progress()
    {
        var points = new List<(long, double?, double)>(Records.Count);
        double? best = null; double cost = 0;
        foreach (EvolutionTraceRecord r in Records)
        {
            cost += r.CostUnits;
            if (Valid(r) && (best is null || Better(r.Quality!.Value, best.Value))) best = r.Quality;
            points.Add((r.Sequence, best, cost));
        }
        return points;
    }

    public object Summary()
    {
        EvolutionTraceRecord? best = Best;
        return new
        {
            Trace = Path,
            RunId = Read.Summary?.RunId,
            Complete = Read.IsComplete,
            Direction = Direction.ToString(),
            Records = Records.Count,
            Statuses = Records.GroupBy(r => r.Status.ToString()).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            Improvements = Records.Count(r => r.IsImprovement),
            TotalCostUnits = Records.Sum(r => r.CostUnits),
            Islands = Records.Select(r => r.Island).Distinct().Count(),
            Operators = Records.GroupBy(r => r.VariationOperatorId ?? "(seed)").OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => new { Proposals = g.Count(), Improvements = g.Count(r => r.IsImprovement) }),
            Best = best is null ? null : new { best.GenomeId, best.Quality, best.Sequence, best.Cell, best.Island },
            FirstFailure = Read.Summary?.FirstFailureMessage
        };
    }

    public object Winner()
    {
        EvolutionTraceRecord best = Best ?? throw new InvalidDataException("The trace has no valid evaluation to export.");
        var byGenome = Records.GroupBy(r => r.GenomeId).ToDictionary(g => g.Key, g => g.Last());
        var lineage = new List<object>();
        for (EvolutionTraceRecord? node = best; node is not null && lineage.Count < 256;
             node = node.ParentGenomeId is { } parent && byGenome.TryGetValue(parent, out var p) ? p : null)
            lineage.Add(new { node.GenomeId, node.Quality, node.VariationOperatorId, node.Sequence });
        return new
        {
            Schema = "aidotnet-evolve-export-v1",
            best.GenomeId,
            best.Quality,
            Direction = Direction.ToString(),
            best.TaskVersionHash,
            best.EvaluatorVersionHash,
            best.ConfigurationHash,
            best.Cell,
            best.Metrics,
            best.Descriptors,
            Lineage = lineage,
            SourceTrace = System.IO.Path.GetFileName(Path),
            Environment = new
            {
                Os = RuntimeInformation.OSDescription,
                Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString()
            },
            Limitations = "Identity, evidence and lineage only: traces carry no program source and no credentials."
        };
    }

    public static object Compare(TraceAnalysis a, TraceAnalysis b)
    {
        static object Side(TraceAnalysis t)
        {
            EvolutionTraceRecord? best = t.Best;
            var progress = t.Progress();
            var reached = best is null ? default : progress.First(p => p.BestSoFar == best.Quality);
            return new
            {
                t.Path,
                Records = t.Records.Count,
                BestQuality = best?.Quality,
                EvaluationsToBest = best is null ? (int?)null : t.Records.TakeWhile(r => r.Sequence <= reached.Sequence).Count(),
                CostToBest = best is null ? (double?)null : reached.CumulativeCost,
                TotalCostUnits = t.Records.Sum(r => r.CostUnits),
                ValidRate = t.Records.Count(Valid) / (double)t.Records.Count
            };
        }
        if (a.Direction != b.Direction) throw new ArgumentException("The traces optimize in different directions.");
        return new { Direction = a.Direction.ToString(), A = Side(a), B = Side(b) };
    }
}

/// <summary>One self-contained HTML page: inline SVG, inline CSS, no network or script dependencies.</summary>
internal static class HtmlReport
{
    public static string Render(TraceAnalysis t)
    {
        var html = new StringBuilder();
        EvolutionTraceRecord? best = t.Best;
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Evolution run report</title><style>")
            .Append("body{font:14px/1.5 system-ui,sans-serif;margin:24px;color:#1b1b1b;background:#fff}h1{font-size:20px}h2{font-size:16px;margin-top:28px}")
            .Append("table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:3px 8px;text-align:left}svg{background:#fafafa;border:1px solid #ddd}")
            .Append(".note{color:#555}@media(prefers-color-scheme:dark){body{background:#141414;color:#e6e6e6}svg{background:#1d1d1d;border-color:#333}td,th{border-color:#444}.note{color:#aaa}}")
            .Append("</style></head><body>");
        html.Append("<h1>Evolution run report</h1><p class=\"note\">Trace ").Append(E(System.IO.Path.GetFileName(t.Path)))
            .Append(" &middot; ").Append(t.Records.Count).Append(" evaluations &middot; direction ").Append(E(t.Direction.ToString()))
            .Append(t.Read.IsComplete ? "" : " &middot; <strong>trace incomplete</strong>").Append("</p>");
        html.Append("<table><tr><th>Best genome</th><td>").Append(E(best?.GenomeId ?? "none")).Append("</td></tr><tr><th>Best quality</th><td>")
            .Append(best?.Quality is { } q ? q.ToString("G6", CultureInfo.InvariantCulture) : "none").Append("</td></tr><tr><th>Improvements</th><td>")
            .Append(t.Records.Count(r => r.IsImprovement)).Append("</td></tr><tr><th>Total cost units</th><td>")
            .Append(t.Records.Sum(r => r.CostUnits).ToString("G6", CultureInfo.InvariantCulture)).Append("</td></tr></table>");
        var progress = t.Progress();
        html.Append("<h2>Progress (best valid quality so far)</h2>").Append(Line(progress.Select(p => ((double)p.Sequence, p.BestSoFar)).ToList(), "evaluation", "quality"));
        html.Append("<h2>Cost (cumulative cost units)</h2>").Append(Line(progress.Select(p => ((double)p.Sequence, (double?)p.CumulativeCost)).ToList(), "evaluation", "cost"));
        html.Append("<h2>Archive (best quality per cell)</h2>").Append(Heatmap(t));
        html.Append("<h2>Lineage of the best program</h2>").Append(Lineage(t, best));
        html.Append("<p class=\"note\">Generated by aidotnet-evolve from trace data only; it opens offline.</p></body></html>");
        return html.ToString();
    }

    private static string E(string text) => System.Net.WebUtility.HtmlEncode(text);
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Line(IReadOnlyList<(double X, double? Y)> points, string xLabel, string yLabel)
    {
        var defined = points.Where(p => p.Y is not null).Select(p => (p.X, Y: p.Y!.Value)).ToList();
        if (defined.Count == 0) return "<p class=\"note\">No valid points.</p>";
        const double w = 640, h = 220, pad = 40;
        double x0 = defined.Min(p => p.X), x1 = Math.Max(defined.Max(p => p.X), x0 + 1);
        double y0 = defined.Min(p => p.Y), y1 = defined.Max(p => p.Y); if (y1 == y0) y1 = y0 + 1;
        string path = string.Join(" ", defined.Select((p, i) => (i == 0 ? "M" : "L") + N(pad + (p.X - x0) / (x1 - x0) * (w - 2 * pad)) + "," +
            N(h - pad - (p.Y - y0) / (y1 - y0) * (h - 2 * pad))));
        return "<svg role=\"img\" aria-label=\"" + E(yLabel) + " by " + E(xLabel) + "\" width=\"" + N(w) + "\" height=\"" + N(h) + "\" viewBox=\"0 0 " + N(w) + " " + N(h) + "\">" +
            "<path d=\"" + path + "\" fill=\"none\" stroke=\"#2f6fdf\" stroke-width=\"2\"/>" +
            "<text x=\"" + N(pad) + "\" y=\"16\" font-size=\"11\" fill=\"currentColor\">" + E(yLabel) + " " + y1.ToString("G4", CultureInfo.InvariantCulture) + "</text>" +
            "<text x=\"" + N(pad) + "\" y=\"" + N(h - 8) + "\" font-size=\"11\" fill=\"currentColor\">" + E(yLabel) + " " + y0.ToString("G4", CultureInfo.InvariantCulture) + " &middot; " + E(xLabel) + " " + N(x0) + "&ndash;" + N(x1) + "</text></svg>";
    }

    private static string Heatmap(TraceAnalysis t)
    {
        var cells = t.Records.Where(r => r.Cell is not null && r.Status == EvolutionEvaluationStatus.Completed && r.Quality is not null)
            .GroupBy(r => r.Cell!).Select(g => (Key: g.Key, Best: t.Direction == EvolutionOptimizationDirection.Minimize ? g.Min(r => r.Quality!.Value) : g.Max(r => r.Quality!.Value)))
            .Select(c => (c.Key, c.Best, Coords: c.Key.Split(',').Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1).ToArray()))
            .Where(c => c.Coords.Length >= 1 && c.Coords.All(v => v >= 0)).ToList();
        if (cells.Count == 0) return "<p class=\"note\">No archive cells in this trace.</p>";
        int cols = cells.Max(c => c.Coords[0]) + 1, rows = cells.Max(c => c.Coords.Length > 1 ? c.Coords[1] : 0) + 1;
        if (cols > 256 || rows > 256) return "<p class=\"note\">Archive too large to draw.</p>";
        double lo = cells.Min(c => c.Best), hi = Math.Max(cells.Max(c => c.Best), lo + 1e-12);
        const int size = 14;
        var svg = new StringBuilder("<svg role=\"img\" aria-label=\"archive heatmap\" width=\"" + (cols * size + 2) + "\" height=\"" + (rows * size + 2) + "\">");
        foreach (var c in cells)
        {
            double f = (c.Best - lo) / (hi - lo); if (t.Direction == EvolutionOptimizationDirection.Minimize) f = 1 - f;
            int shade = (int)Math.Round(40 + f * 180);
            svg.Append("<rect x=\"").Append(1 + c.Coords[0] * size).Append("\" y=\"").Append(1 + (c.Coords.Length > 1 ? c.Coords[1] : 0) * size)
               .Append("\" width=\"").Append(size - 1).Append("\" height=\"").Append(size - 1).Append("\" fill=\"rgb(40,").Append(shade).Append(",").Append(255 - shade / 2)
               .Append(")\"><title>").Append(E(c.Key)).Append(": ").Append(c.Best.ToString("G6", CultureInfo.InvariantCulture)).Append("</title></rect>");
        }
        return svg.Append("</svg><p class=\"note\">").Append(cells.Count).Append(" occupied cells; brighter is better.</p>").ToString();
    }

    private static string Lineage(TraceAnalysis t, EvolutionTraceRecord? best)
    {
        if (best is null) return "<p class=\"note\">No valid program.</p>";
        var byGenome = t.Records.GroupBy(r => r.GenomeId).ToDictionary(g => g.Key, g => g.Last());
        var rows = new StringBuilder("<table><tr><th>#</th><th>Genome</th><th>Quality</th><th>Operator</th></tr>");
        int depth = 0;
        for (EvolutionTraceRecord? node = best; node is not null && depth < 64;
             node = node.ParentGenomeId is { } parent && byGenome.TryGetValue(parent, out var p) ? p : null, depth++)
            rows.Append("<tr><td>").Append(node.Sequence).Append("</td><td>").Append(E(node.GenomeId)).Append("</td><td>")
                .Append(node.Quality?.ToString("G6", CultureInfo.InvariantCulture) ?? "").Append("</td><td>").Append(E(node.VariationOperatorId ?? "(seed)")).Append("</td></tr>");
        return rows.Append("</table>").ToString();
    }
}