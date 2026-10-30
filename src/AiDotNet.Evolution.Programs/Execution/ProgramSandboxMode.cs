// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Enums/ProgramSandboxMode.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs;

/// <summary>Requested execution boundary. The process runner implements only OutOfProcessWorker.</summary>
/// <remarks>Separate processes do not isolate filesystem, network, identity or system calls, so every mode except
/// <see cref="Container"/> and <see cref="Serving"/> requires <see cref="ProgramSandboxOptions.AllowUnsandboxedExecution"/>.
/// <see cref="Container"/> is the shipped boundary for hostile programs.</remarks>
public enum ProgramSandboxMode
{
    /// <summary>Child process with bounded output and time; memory enforcement is attempted, not attested.</summary>
    OutOfProcessWorker = 0,
    /// <summary>Caller-provisioned remote boundary; no serving connector is included in this package.</summary>
    Serving = 1,
    /// <summary>Caller-supplied unsafe in-process execution; never implemented by the process runner.</summary>
    InProcessUnsafe = 2,
    /// <summary>A pre-started Python interpreter forks a fresh, limit-bearing child per candidate (Linux and macOS);
    /// run by <see cref="WarmPythonExecutionEngine"/>.</summary>
    WarmForkWorker = 3,
    /// <summary>A pre-started Python interpreter runs candidates one after another in fresh namespaces, replaced after a
    /// timeout, a failure or a set count; weaker isolation, run by <see cref="WarmPythonExecutionEngine"/>.</summary>
    WarmReusedWorker = 4,
    /// <summary>A Docker container with no network, a read-only root, a non-root user, no capabilities, the default
    /// seccomp profile and memory, CPU and process caps; run by <see cref="ContainerExecutionEngine"/>.</summary>
    Container = 5
}
