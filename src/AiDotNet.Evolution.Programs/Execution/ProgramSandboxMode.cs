// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Enums/ProgramSandboxMode.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs;

/// <summary>Requested execution boundary. The process runner implements only OutOfProcessWorker.</summary>
/// <remarks>Separate processes do not isolate filesystem, network, identity or system calls.
/// Hostile programs require an independently provisioned OS/container boundary.</remarks>
public enum ProgramSandboxMode
{
    /// <summary>Child process with bounded output and time; memory enforcement is attempted, not attested.</summary>
    OutOfProcessWorker = 0,
    /// <summary>Caller-provisioned remote boundary; no serving connector is included in this package.</summary>
    Serving = 1,
    /// <summary>Caller-supplied unsafe in-process execution; never implemented by the process runner.</summary>
    InProcessUnsafe = 2
}
