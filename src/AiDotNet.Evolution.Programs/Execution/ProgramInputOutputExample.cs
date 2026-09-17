// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Models/ProgramInputOutputExample.cs
// Original license retained in ../Legacy/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs;

/// <summary>
/// Represents a single input-output example for program synthesis.
/// </summary>
/// <remarks>
/// <para>
/// Examples can be used to guide generation (inductive synthesis) and to validate candidate programs
/// (execution-based evaluation).
/// </para>
/// <para><b>For Beginners:</b> This is one example of what the program should do.
///
/// It says: "When the program gets this input, it should produce this output."
/// </para>
/// </remarks>
public sealed class ProgramInputOutputExample
{
    public string Input { get; set; } = string.Empty;

    public string ExpectedOutput { get; set; } = string.Empty;
}
