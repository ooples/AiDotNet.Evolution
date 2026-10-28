using System.Diagnostics;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Execution;

/// <summary>Whether this machine can run the container engine's tests, and why not when it cannot.</summary>
/// <remarks>
/// The container tests skip, with the reason, only where Docker is genuinely absent: no daemon, a daemon running
/// Windows containers, or the test image not pulled. Setting <c>AIDOTNET_EVOLUTION_REQUIRE_DOCKER=1</c> turns every
/// such skip into a failure; CI's Linux lane sets it, so a runner that lost Docker fails rather than passing green.
/// </remarks>
internal static class DockerTestEnvironment
{
    private static readonly Lazy<string?> Unavailable = new(Probe);

    /// <summary>The image the tests run candidates in; <c>AIDOTNET_EVOLUTION_TEST_IMAGE</c> overrides it.</summary>
    internal static string Image => Environment.GetEnvironmentVariable("AIDOTNET_EVOLUTION_TEST_IMAGE") is { Length: > 0 } image
        ? image
        : "python:3.12-slim";

    /// <summary>Whether a missing Docker must fail the tests instead of skipping them.</summary>
    internal static bool Required => Environment.GetEnvironmentVariable("AIDOTNET_EVOLUTION_REQUIRE_DOCKER") == "1";

    /// <summary>Why the container tests cannot run here, or <c>null</c> when they can.</summary>
    internal static string? UnavailableReason => Unavailable.Value;

    internal static string? SkipReason => Required || UnavailableReason is null
        ? null
        : "Docker is unavailable: " + UnavailableReason + ". Set AIDOTNET_EVOLUTION_REQUIRE_DOCKER=1 to fail instead of skipping.";

    /// <summary>Runs the Docker client and returns its exit code and standard output, or <c>null</c> when it cannot start.</summary>
    internal static (int ExitCode, string StdOut)? Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("docker did not start");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            Task.WaitAll(stdout, stderr);
            return (process.ExitCode, stdout.Result);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Probe()
    {
        (int ExitCode, string StdOut)? info = Docker("info", "--format", "{{.OSType}}");
        if (info is null || info.Value.ExitCode != 0) return "the docker client is missing or its daemon is not running";
        string os = info.Value.StdOut.Trim();
        if (os != "linux") return "the daemon runs '" + os + "' containers, not Linux containers";
        (int ExitCode, string StdOut)? image = Docker("image", "inspect", "--format", "{{.Id}}", Image);
        if (image is null || image.Value.ExitCode != 0) return "the test image " + Image + " is not pulled (docker pull " + Image + ")";
        return null;
    }
}

/// <summary>A fact that needs a Linux-container Docker daemon and the test image.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    /// <summary>Skips with the reason when Docker is absent, unless <c>AIDOTNET_EVOLUTION_REQUIRE_DOCKER=1</c>.</summary>
    public DockerFactAttribute()
    {
        if (DockerTestEnvironment.SkipReason is { } reason) Skip = reason;
    }
}

/// <summary>A theory that needs a Linux-container Docker daemon and the test image.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    /// <summary>Skips with the reason when Docker is absent, unless <c>AIDOTNET_EVOLUTION_REQUIRE_DOCKER=1</c>.</summary>
    public DockerTheoryAttribute()
    {
        if (DockerTestEnvironment.SkipReason is { } reason) Skip = reason;
    }
}
