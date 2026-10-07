using System.Text.Json;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

/// <summary>A real sm_75 kernel and its contract: out[i] = a * x[i] + y[i] for i &lt; n.</summary>
internal static class Axpy
{
    internal const string Source = """
.version 6.4
.target sm_75
.address_size 64

.visible .entry axpy(
    .param .u64 x,
    .param .u64 y,
    .param .u64 out,
    .param .f32 a,
    .param .s32 n
)
{
    .reg .pred %p<2>;
    .reg .b32 %r<6>;
    .reg .f32 %f<5>;
    .reg .b64 %rd<11>;

    ld.param.u64 %rd1, [x];
    ld.param.u64 %rd2, [y];
    ld.param.u64 %rd3, [out];
    ld.param.f32 %f1, [a];
    ld.param.u32 %r1, [n];
    mov.u32 %r2, %ctaid.x;
    mov.u32 %r3, %ntid.x;
    mov.u32 %r4, %tid.x;
    mad.lo.s32 %r5, %r2, %r3, %r4;
    setp.ge.s32 %p1, %r5, %r1;
    @%p1 bra DONE;
    cvta.to.global.u64 %rd4, %rd1;
    cvta.to.global.u64 %rd5, %rd2;
    cvta.to.global.u64 %rd6, %rd3;
    mul.wide.s32 %rd7, %r5, 4;
    add.s64 %rd8, %rd4, %rd7;
    add.s64 %rd9, %rd5, %rd7;
    add.s64 %rd10, %rd6, %rd7;
    ld.global.f32 %f2, [%rd8];
    ld.global.f32 %f3, [%rd9];
    fma.rn.f32 %f4, %f1, %f2, %f3;
    st.global.f32 [%rd10], %f4;
DONE:
    ret;
}
""";

    internal const string Fma = "    fma.rn.f32 %f4, %f1, %f2, %f3;";
    internal static string Wrong => Source.Replace(Fma, "    fma.rn.f32 %f4, %f1, %f2, %f2;", StringComparison.Ordinal);
    internal static string Unguarded => Source.Replace("    @%p1 bra DONE;\n", string.Empty, StringComparison.Ordinal);
    internal static string Hanging => Source.Replace("DONE:\n    ret;", "DONE:\nSPIN:\n    bra SPIN;\n    ret;", StringComparison.Ordinal);
    internal static string Block128 => "// aidotnet-launch: block=128,1,1\n" + Source;

    internal static PtxKernelContract Contract(int fuzzCases = 8, long timingN = 1 << 16) => new(
        "axpy-f32", "axpy", "out[i] = a * x[i] + y[i] for every i < n.", PtxTargetLimits.ForSm(75),
        new[]
        {
            PtxKernelParameter.Input("x", PtxElementType.Float32, PtxExtent.Of("N")),
            PtxKernelParameter.Input("y", PtxElementType.Float32, PtxExtent.Of("N")),
            PtxKernelParameter.Output("out", PtxElementType.Float32, PtxExtent.Of("N")),
            PtxKernelParameter.ScalarConstant("a", PtxElementType.Float32, 2.0),
            PtxKernelParameter.Scalar("n", PtxElementType.Int32, PtxExtent.Of("N"))
        },
        new[] { new PtxShapeSymbol("N", 1, 4096, new long[] { 256 }) },
        new PtxLaunchConfiguration(PtxExtent.Of("N").CeilDiv(PtxExtent.BlockX), null, null, 256, 1, 1, null),
        PtxTolerance.Exact, new Dictionary<string, long> { ["N"] = timingN },
        new IReadOnlyDictionary<string, long>[] { new Dictionary<string, long> { ["N"] = 1000 } }, fuzzCases, 7);

    internal static IPtxKernelReference Reference { get; } = PtxKernelReference.Create("axpy-cpu-v1", call =>
    {
        ReadOnlySpan<float> x = call.Input<float>("x"), y = call.Input<float>("y");
        Span<float> output = call.Output<float>("out");
        float a = (float)call.Scalar("a");
        for (int i = 0; i < output.Length; i++) output[i] = MathF.FusedMultiplyAdd(a, x[i], y[i]);
    });
}

/// <summary>What a simulated kernel does wrong, selected by a marker comment in its PTX.</summary>
public enum SimulatedFault
{
    None,
    WrongValue,
    SkipLastElement,
    WriteOutOfBounds,
    LaunchFault
}

/// <summary>
/// An in-process stand-in for the GPU worker, so the evaluators' logic is tested offline: it regenerates the exact seeded
/// inputs the real worker would, runs a C# model of axpy, and reproduces the worker's sentinel, guard and fault behaviour.
/// </summary>
internal sealed class FakeWorkerTransport : IPtxWorkerTransport
{
    internal const string FaultMarker = "// simulate:";
    public string Identity => "fake-worker";
    internal List<PtxWorkerRequest> Requests { get; } = new();
    internal string Status { get; set; } = "ok";
    internal PtxWorkerExchangeStatus ExchangeStatus { get; set; } = PtxWorkerExchangeStatus.Completed;
    internal int ComputeMajor { get; set; } = 7;
    internal int ComputeMinor { get; set; } = 5;
    internal Func<PtxWorkerKernel, PtxWorkerCompiled> Compile { get; set; } = _ => new PtxWorkerCompiled
    {
        Loaded = true,
        Registers = 12,
        MaxThreadsPerBlock = 1024,
        WallTimeMilliseconds = 3.5,
        PtxVersion = 64,
        BinaryVersion = 75
    };
    internal Func<int, (double Candidate, double Incumbent)> Pair { get; set; } = i => (1.0 + (i % 3) * 0.01, 1.25 + (i % 5) * 0.01);
    internal Func<int, (double First, double Second)> Control { get; set; } = i => (1.25 + (i % 2) * 0.01, 1.25);

    public PtxWorkerExchange Exchange(PtxWorkerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        if (ExchangeStatus != PtxWorkerExchangeStatus.Completed) return new(ExchangeStatus, null, null, "simulated", TimeSpan.FromSeconds(1));
        var response = new PtxWorkerResponse
        {
            Status = Status,
            Device = new PtxWorkerDevice { Name = "Simulated GPU", ComputeMajor = ComputeMajor, ComputeMinor = ComputeMinor, DriverVersion = 13040, MaxSharedMemoryPerBlockOptin = 65536 }
        };
        if (Status != "ok" || request.Operation == PtxWorkerOperation.Probe) return Done(response);
        response.Kernels.AddRange(request.Kernels.Select(Compile));
        if (request.Operation == PtxWorkerOperation.Compile) return Done(response);
        if (request.Operation == PtxWorkerOperation.Time)
        {
            var timing = new PtxWorkerTimingResult();
            PtxWorkerTimingPlan plan = request.Timing ?? throw new InvalidOperationException("A timing request needs a plan.");
            for (int i = 0; i < plan.ControlPairs; i++)
            {
                (double first, double second) = Control(i);
                timing.ControlFirstMilliseconds.Add(first);
                timing.ControlSecondMilliseconds.Add(second);
            }
            for (int i = 0; i < plan.Pairs; i++)
            {
                (double candidate, double incumbent) = Pair(i);
                timing.CandidateMilliseconds.Add(candidate);
                timing.IncumbentMilliseconds.Add(incumbent);
            }
            response.Timing = timing;
            return Done(response);
        }
        foreach (PtxWorkerCase item in request.Cases)
        {
            var result = new PtxWorkerCaseResult();
            response.Cases.Add(result);
            foreach (PtxWorkerKernel kernel in request.Kernels) result.Runs.Add(Run(kernel, item));
            if (result.Runs.Any(r => r.Status != "ok"))
            {
                response.Status = "context-lost";
                break;
            }
        }
        return Done(response);
    }

    private static PtxWorkerExchange Done(PtxWorkerResponse response) => new(PtxWorkerExchangeStatus.Completed, response, 0, string.Empty, TimeSpan.FromMilliseconds(5));

    internal static SimulatedFault FaultOf(string ptx)
    {
        int at = ptx.IndexOf(FaultMarker, StringComparison.Ordinal);
        if (at < 0) return SimulatedFault.None;
        string name = ptx.Substring(at + FaultMarker.Length).Split('\n')[0].Trim();
        return Enum.Parse<SimulatedFault>(name);
    }

    private static PtxWorkerRun Run(PtxWorkerKernel kernel, PtxWorkerCase item)
    {
        SimulatedFault fault = FaultOf(kernel.Ptx);
        if (fault == SimulatedFault.LaunchFault) return new PtxWorkerRun { Status = "CUDA_ERROR_ILLEGAL_ADDRESS", Message = "simulated fault" };
        float[] x = Floats(PtxDeterministicFill.Generate(item.Buffers[0])), y = Floats(PtxDeterministicFill.Generate(item.Buffers[1]));
        float a = BitConverter.ToSingle(Convert.FromBase64String(item.Arguments[3].ScalarBase64 ?? throw new InvalidOperationException("a is a scalar.")));
        var run = new PtxWorkerRun();
        foreach ((byte sentinel, List<string> outputs) in new[] { ((byte)0xFF, run.FirstOutputs), ((byte)0x00, run.SecondOutputs) })
        {
            var bytes = new byte[x.Length * 4];
            Array.Fill(bytes, sentinel);
            int written = fault == SimulatedFault.SkipLastElement ? x.Length - 1 : x.Length;
            for (int i = 0; i < written; i++)
            {
                float value = MathF.FusedMultiplyAdd(a, x[i], fault == SimulatedFault.WrongValue ? x[i] : y[i]);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), value);
            }
            outputs.Add(Convert.ToBase64String(bytes));
        }
        if (fault == SimulatedFault.WriteOutOfBounds && item.Buffers[2].Elements % 256 != 0) run.GuardViolations.Add(2);
        return run;
    }

    private static float[] Floats(byte[] bytes)
    {
        var values = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
}

/// <summary>A scripted chat client: no network, no spend.</summary>
internal sealed class ScriptedChatClient : IProgramChatClient
{
    internal Func<int, IReadOnlyList<ProgramChatMessage>, string> Reply { get; set; } = (_, _) => "{}";
    public string ModelId { get; set; } = "scripted-ptx-test";
    internal List<IReadOnlyList<ProgramChatMessage>> Conversations { get; } = new();
    internal ProgramChatOptions? LastOptions { get; private set; }

    public Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Conversations.Add(messages.ToArray());
        LastOptions = options;
        string text = Reply(Conversations.Count, messages);
        return Task.FromResult(new ProgramChatResponse(ProgramChatMessage.Assistant(text), new ProgramChatUsage(1000, 500), ModelId));
    }

    internal static string Rewrite(string parentId, string ptx, object? launch = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["schemaVersion"] = 1,
        ["parentId"] = parentId,
        ["hypothesis"] = "A smaller block raises occupancy for this memory-bound kernel.",
        ["rewrite"] = ptx,
        ["launch"] = launch
    }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));
}

/// <summary>Runs a test only where a CUDA device answers the isolated worker's probe; everywhere else it is skipped.</summary>
internal sealed class CudaFactAttribute : FactAttribute
{
    private static readonly Lazy<PtxDeviceProbe> Probe = new(() => PtxDeviceProbe.Run(new PtxIsolationOptions { Timeout = TimeSpan.FromSeconds(60) }));

    public CudaFactAttribute()
    {
        PtxDeviceProbe probe = Probe.Value;
        if (!probe.IsAvailable) Skip = "No CUDA device: " + probe.Message;
        else if (probe.Device is { SmVersion: not 75 } device) Skip = "These kernels target sm_75; this device is sm_" + device.SmVersion + ".";
    }
}