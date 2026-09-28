using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiDotNet.Evolution.Programs;

// V1-76 (#181): what process isolation adds to one evaluation of a trivial Python candidate.
// Usage: EvolutionSandbox <python> <evaluations> [warmup]
//   baseline  one long-lived interpreter that execs each candidate in-process and replies on a pipe, the way
//             OpenEvolve's process pool evaluates; its round trip is the cost of evaluation without isolation.
//   process   ProcessProgramExecutionEngine: a fresh, limit-bearing process per candidate, killed as a tree.
//   warm      WarmPythonExecutionEngine: a forked child per candidate on Linux and macOS, a reused interpreter on Windows.
// Added latency is sandbox minus baseline at each percentile.
if (args.Length is < 2 or > 3 || !int.TryParse(args[1], out int evaluations) || evaluations is < 10 or > 100_000)
{
    Console.Error.WriteLine("Usage: EvolutionSandbox <python> <evaluations 10..100000> [warmup]");
    return 2;
}
string python = args[0];
int warmup = args.Length == 3 && int.TryParse(args[2], out int w) && w >= 0 ? w : 20;
const string Candidate = "def solve(x):\n    return x * 2\n\nprint(solve(21))\n";

double[] baseline = await Baseline(python, Candidate, evaluations, warmup);
double[] process = await Sandbox(python, Candidate, evaluations, warmup, ProgramSandboxMode.OutOfProcessWorker);
// The fork worker needs fork(); on Windows only the reused worker is measured.
ProgramSandboxMode warmMode = OperatingSystem.IsWindows() ? ProgramSandboxMode.WarmReusedWorker : ProgramSandboxMode.WarmForkWorker;
double[] warm = await Sandbox(python, Candidate, evaluations, warmup, warmMode);
Console.WriteLine(JsonSerializer.Serialize(new
{
    Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Python = python, Evaluations = evaluations,
    BaselineMs = Summary(baseline), ProcessMs = Summary(process),
    AddedP50Ms = Percentile(process, 0.50) - Percentile(baseline, 0.50),
    AddedP95Ms = Percentile(process, 0.95) - Percentile(baseline, 0.95),
    WarmMode = warmMode.ToString(), WarmMs = Summary(warm),
    WarmAddedP50Ms = Percentile(warm, 0.50) - Percentile(baseline, 0.50),
    WarmAddedP95Ms = Percentile(warm, 0.95) - Percentile(baseline, 0.95)
}));
return 0;

static object Summary(double[] ms) => new { P50 = Percentile(ms, 0.50), P95 = Percentile(ms, 0.95), Mean = ms.Average() };

static double Percentile(double[] values, double q)
{
    double[] sorted = values.OrderBy(v => v).ToArray();
    return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
}

static async Task<double[]> Baseline(string python, string candidate, int evaluations, int warmup)
{
    // Reads a length-prefixed source, execs it with stdout captured, and replies with the captured text.
    const string Worker = "import sys, io, contextlib\n" +
        "inp = sys.stdin.buffer; out = sys.stdout.buffer\n" +
        "while True:\n" +
        "    header = inp.readline()\n" +
        "    if not header: break\n" +
        "    source = inp.read(int(header)).decode()\n" +
        "    buffer = io.StringIO()\n" +
        "    with contextlib.redirect_stdout(buffer): exec(source, {})\n" +
        "    reply = buffer.getvalue().encode()\n" +
        "    out.write(str(len(reply)).encode() + b'\\n' + reply); out.flush()\n";
    var start = new ProcessStartInfo(python) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    start.ArgumentList.Add("-u");
    start.ArgumentList.Add("-c");
    start.ArgumentList.Add(Worker);
    using Process worker = Process.Start(start) ?? throw new InvalidOperationException("Worker did not start.");
    Stream input = worker.StandardInput.BaseStream, output = worker.StandardOutput.BaseStream;
    byte[] source = Encoding.UTF8.GetBytes(candidate);
    byte[] header = Encoding.ASCII.GetBytes(source.Length + "\n");
    var times = new List<double>(evaluations);
    for (int i = 0; i < warmup + evaluations; i++)
    {
        long begin = Stopwatch.GetTimestamp();
        await input.WriteAsync(header);
        await input.WriteAsync(source);
        await input.FlushAsync();
        string reply = Encoding.UTF8.GetString(await ReadFrame(output));
        double ms = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        if (reply.Trim() != "42") throw new InvalidOperationException("Baseline worker returned " + reply);
        if (i >= warmup) times.Add(ms);
    }
    input.Close();
    worker.WaitForExit();
    return times.ToArray();
}

static async Task<byte[]> ReadFrame(Stream stream)
{
    var header = new StringBuilder();
    int b;
    while ((b = stream.ReadByte()) != '\n')
    {
        if (b < 0) throw new EndOfStreamException("Worker closed its output.");
        header.Append((char)b);
    }
    byte[] body = new byte[int.Parse(header.ToString())];
    int read = 0;
    while (read < body.Length)
    {
        int n = await stream.ReadAsync(body.AsMemory(read));
        if (n == 0) throw new EndOfStreamException("Worker closed its output mid-reply.");
        read += n;
    }
    return body;
}

static async Task<double[]> Sandbox(string python, string candidate, int evaluations, int warmup, ProgramSandboxMode mode)
{
    var options = new ProgramSandboxOptions { Mode = mode, AllowUnsafeInProcessExecution = mode == ProgramSandboxMode.WarmReusedWorker };
    options.SetInterpreter(ProgramLanguage.Python, new ProgramInterpreterSpecification(python, "{source}"));
    options.Limits.MaxConcurrentExecutions = 1;
    IProgramExecutionEngine engine = mode == ProgramSandboxMode.OutOfProcessWorker
        ? new ProcessProgramExecutionEngine(options)
        : new WarmPythonExecutionEngine(options, recycleAfter: 100_000);
    using var owned = (IDisposable)engine;
    var times = new List<double>(evaluations);
    for (int i = 0; i < warmup + evaluations; i++)
    {
        long begin = Stopwatch.GetTimestamp();
        ProgramExecuteResponse response = await engine.ExecuteAsync(new ProgramExecuteRequest
        {
            Language = ProgramLanguage.Python, SourceCode = candidate
        });
        double ms = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        if (!response.Success || response.StdOut?.Trim() != "42") throw new InvalidOperationException("Sandbox run failed: " + response.Error);
        if (i >= warmup) times.Add(ms);
    }
    return times.ToArray();
}
