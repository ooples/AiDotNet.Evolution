using AiDotNet.Evolution.CSharp;
using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Ptx;

var compiler = new CSharpProgramCompiler(new[] { typeof(object).Assembly.Location }, "release-package-consumer");
var source = new ProgramSnapshot(new Dictionary<string, string> { ["Candidate.cs"] = "public static class Candidate { public static int Run() => 42; }" });
if (compiler.Catalog(source).Count == 0)
    throw new InvalidOperationException("Packaged compiler did not discover edit targets.");
Console.WriteLine("Packaged CSharp compiler loaded Roslyn and cataloged a program.");

// The packaged PTX module: a data-only contract round-trips, and the embedded isolated worker is present.
var contract = new PtxKernelContract("release-consumer", "k", null, PtxTargetLimits.ForSm(75),
    new[] { PtxKernelParameter.Output("y", PtxElementType.Float32, PtxExtent.Of("N")) },
    new[] { new PtxShapeSymbol("N", 1, 1024, new long[] { 256 }) },
    new PtxLaunchConfiguration(PtxExtent.Of("N").CeilDiv(PtxExtent.BlockX), null, null, 256, 1, 1, null),
    PtxTolerance.Exact, new Dictionary<string, long> { ["N"] = 1024 }, null, 4, 1);
if (PtxKernelContract.FromJson(contract.ToJson()).Fingerprint != contract.Fingerprint)
    throw new InvalidOperationException("Packaged PTX contract did not round-trip.");
if (typeof(PtxProgramCompiler).Assembly.GetManifestResourceInfo("AiDotNet.Evolution.Ptx.Worker.dll") is null)
    throw new InvalidOperationException("Packaged PTX module is missing its isolated worker.");
Console.WriteLine("Packaged PTX module round-tripped a contract and carries its worker.");