using AiDotNet.Evolution.CSharp;
using AiDotNet.Evolution.Programs;

var compiler = new CSharpProgramCompiler(new[] { typeof(object).Assembly.Location }, "release-package-consumer");
var source = new ProgramSnapshot(new Dictionary<string, string> { ["Candidate.cs"] = "public static class Candidate { public static int Run() => 42; }" });
if (compiler.Catalog(source).Count == 0)
    throw new InvalidOperationException("Packaged compiler did not discover edit targets.");
Console.WriteLine("Packaged CSharp compiler loaded Roslyn and cataloged a program.");
