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

// The release packages carry ReadyToRun images (E-1): on a runtime they cover, a portable app must load the image for
// that runtime, not the IL beside it.
string runtime = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
if (new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" }.Contains(runtime))
{
    foreach (string name in new[] { "AiDotNet.Evolution", "AiDotNet.Evolution.Programs", "AiDotNet.Evolution.CSharp", "AiDotNet.Evolution.Ptx" })
    {
        string location = System.Reflection.Assembly.Load(name).Location;
        string expected = Path.Combine("runtimes", runtime, "lib");
        if (!location.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name} loaded from {location}, not its {runtime} ReadyToRun image.");
        using var image = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(location));
        if (image.PEHeaders.CorHeader is not { ManagedNativeHeaderDirectory.Size: > 0 })
            throw new InvalidOperationException($"{name} at {location} is not precompiled.");
    }
    Console.WriteLine($"Packaged assemblies loaded as {runtime} ReadyToRun images.");
}
else
{
    Console.WriteLine($"No ReadyToRun images ship for {runtime}; the IL was loaded.");
}