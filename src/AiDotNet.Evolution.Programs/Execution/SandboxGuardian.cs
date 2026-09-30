using System.Diagnostics;
using System.Reflection;

namespace AiDotNet.Evolution.Programs;

/// <summary>
/// Makes the embedded Linux sandbox guardian runnable for one engine: writes it into a private directory, finds the
/// <c>dotnet</c> host, and checks that it can really become a child subreaper before any candidate depends on it.
/// </summary>
/// <remarks>
/// The directory is created fresh per engine with a random name and, where the platform allows, owner-only
/// permissions: the shared sandbox root usually sits under <c>/tmp</c>, where a fixed name could be planted by another
/// local user before the engine wrote it. Anything that fails - no <c>dotnet</c> host, a seccomp profile that refuses
/// <c>prctl</c> - leaves the engine on its previous behaviour (the live-tree walk) rather than failing executions.
/// </remarks>
internal sealed class SandboxGuardian
{
    private const string AssemblyResource = "AiDotNet.Evolution.Programs.Execution.SandboxGuardian.dll";
    private const string RuntimeConfigResource = "AiDotNet.Evolution.Programs.Execution.SandboxGuardian.runtimeconfig.json";
    private const string AssemblyFileName = "AiDotNet.Evolution.Sandbox.Guardian.dll";
    private const string RuntimeConfigFileName = "AiDotNet.Evolution.Sandbox.Guardian.runtimeconfig.json";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private SandboxGuardian(string host, string assemblyPath, string directory)
    {
        Host = host;
        AssemblyPath = assemblyPath;
        Directory = directory;
    }

    /// <summary>The <c>dotnet</c> host that runs the guardian.</summary>
    public string Host { get; }

    /// <summary>The written guardian assembly.</summary>
    public string AssemblyPath { get; }

    /// <summary>The private directory the guardian was written to; deleted with the engine.</summary>
    public string Directory { get; }

    /// <summary>Writes and probes the guardian, or returns <c>null</c> when it cannot contain candidates here.</summary>
    /// <param name="workspaceRoot">The engine's sandbox root; the guardian gets a private directory below it.</param>
    /// <param name="shellPath">The shell the probe runs under the guardian, as a candidate would.</param>
    public static SandboxGuardian? TryPrepare(string workspaceRoot, string shellPath)
    {
        string? host = FindDotnetHost();
        if (host is null) return null;

        string directory = Path.Combine(workspaceRoot, "guardian-" + Guid.NewGuid().ToString("N"));
        try
        {
            CreatePrivateDirectory(directory);
            string assemblyPath = Path.Combine(directory, AssemblyFileName);
            if (!TryWriteResource(AssemblyResource, assemblyPath)
                || !TryWriteResource(RuntimeConfigResource, Path.Combine(directory, RuntimeConfigFileName)))
            {
                TryDelete(directory);
                return null;
            }

            var guardian = new SandboxGuardian(host, assemblyPath, directory);
            if (guardian.Probe(shellPath)) return guardian;
            TryDelete(directory);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(directory);
            return null;
        }
    }

    /// <summary>Deletes the guardian's private directory.</summary>
    public void Delete() => TryDelete(Directory);

    // One real run: the guardian refuses to start without PR_SET_CHILD_SUBREAPER, so a clean exit proves it works here.
    private bool Probe(string shellPath)
    {
        var start = new ProcessStartInfo(Host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(AssemblyPath);
        start.ArgumentList.Add(shellPath);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exit 0");
        try
        {
            using Process? probe = Process.Start(start);
            if (probe is null) return false;
            if (!probe.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                try { probe.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }

            return probe.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? FindDotnetHost()
    {
        // Set by the SDK and test hosts; otherwise the running process may itself be the muxer.
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath))
            return hostPath;
        if (Environment.ProcessPath is { } processPath
            && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.Ordinal))
            return processPath;
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root
            && File.Exists(Path.Combine(root, "dotnet")))
            return Path.Combine(root, "dotnet");
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (entry.Length == 0) continue;
            string candidate = Path.Combine(entry, "dotnet");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static bool TryWriteResource(string resource, string path)
    {
        using Stream? source = typeof(SandboxGuardian).GetTypeInfo().Assembly.GetManifestResourceStream(resource);
        if (source is null) return false;
        using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
        return true;
    }

    private static void CreatePrivateDirectory(string directory)
    {
#if NET7_0_OR_GREATER
        if (!OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }
#endif
        System.IO.Directory.CreateDirectory(directory);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temp directory: nothing to do if it cannot be removed now.
        }
    }
}