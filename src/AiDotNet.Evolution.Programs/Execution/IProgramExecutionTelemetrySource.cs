// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Interfaces/IProgramExecutionTelemetrySource.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs;

/// <summary>Optional, nonblocking and thread-safe runner-instance counters; individual reads are not a joint atomic snapshot.</summary>
public interface IProgramExecutionTelemetrySource
{
    /// <summary>Gets requests waiting for execution capacity on this instance.</summary>
    int QueuedExecutionCount { get; }
    /// <summary>Gets requests currently holding execution capacity on this instance.</summary>
    int ActiveExecutionCount { get; }
}
