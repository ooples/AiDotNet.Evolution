namespace AiDotNet.Evolution.Programs;

/// <summary>How much reasoning a model that supports it should spend on a response (OpenEvolve's <c>reasoning_effort</c>).</summary>
public enum ProgramReasoningEffort
{
    /// <summary>The least reasoning; fastest and cheapest.</summary>
    Low,
    /// <summary>A balance of cost and depth.</summary>
    Medium,
    /// <summary>The most reasoning the model offers.</summary>
    High
}
