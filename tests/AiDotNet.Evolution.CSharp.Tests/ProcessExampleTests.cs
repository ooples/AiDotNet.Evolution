using System.Reflection;
using System.Text.Json;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

public sealed class ProcessExampleTests
{
    private static int Invoke(params string[] args) =>
        (int)Assembly.Load("CompilerGuidedSearch").EntryPoint!.Invoke(null, new object[] { args })!;

    [Fact]
    public void InvalidArgumentsDoNotStartExecution() => Assert.Equal(64, Invoke());

    [Fact]
    public void RealProcessFixtureRetainsFailedAttemptAndExactConfirmation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "us17-process-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, Invoke(directory));
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "trusted-smoke", "result.json")));
        Assert.Equal(2, result.RootElement.GetProperty("Attempts").GetInt32());
        var candidate = result.RootElement.GetProperty("Candidate");
        var confirmation = result.RootElement.GetProperty("CandidateConfirmation");
        Assert.True(confirmation.GetProperty("Correct").GetBoolean());
        Assert.Equal(candidate.GetProperty("Fingerprint").GetString(), confirmation.GetProperty("ArtifactFingerprint").GetString());
        using var failed = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "trusted-smoke", "attempt-1-test.json")));
        Assert.False(failed.RootElement.GetProperty("Correct").GetBoolean());
        Assert.True(File.Exists(Path.Combine(directory, "final-ledger.json")));
    }
}
