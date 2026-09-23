// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Enums/ProgramMetricValueKind.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.Globalization;
namespace AiDotNet.Evolution.Programs;

/// <summary>Standalone ProgramMetricValueKind.</summary>
public enum ProgramMetricValueKind
{
    /// <summary>A real-valued measurement that may participate in an aggregation.</summary>
    Number = 0,

    /// <summary>A boolean flag such as a timeout indicator, which is never averaged as a score.</summary>
    Flag = 1,

    /// <summary>Free text such as an error message or a label.</summary>
    Text = 2
}
