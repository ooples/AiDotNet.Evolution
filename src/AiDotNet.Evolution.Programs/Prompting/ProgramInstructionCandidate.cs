using System.Globalization;
using AiDotNet.Evolution;

namespace AiDotNet.Evolution.Programs;

/// <summary>
/// One evolved system instruction in the meta-prompt archive, with its lineage and the downstream
/// quality of the programs it produced (V1-53, #166).
/// </summary>
/// <remarks>
/// An instruction is scored by what the programs written under it achieved, not by anything the
/// model says about it: <see cref="MeanQuality"/> is the mean primary quality of the evaluations
/// routed to this instruction. That is the only signal that cannot be talked up by the model being
/// asked to improve its own instructions.
/// </remarks>
public sealed class ProgramInstructionCandidate
{
    internal ProgramInstructionCandidate(string id, string text, string? parentId, long generation)
    {
        Id = id;
        Text = text;
        ParentId = parentId;
        Generation = generation;
    }

    /// <summary>A stable id, derived from the instruction text, so the same text is the same candidate.</summary>
    public string Id { get; }

    /// <summary>The instruction itself.</summary>
    public string Text { get; }

    /// <summary>The instruction this one was proposed from, or null for the run's seed instruction.</summary>
    public string? ParentId { get; }

    /// <summary>The generation at which this instruction entered the archive.</summary>
    public long Generation { get; }

    /// <summary>How many proposals have been written under this instruction.</summary>
    public long Uses { get; internal set; }

    /// <summary>How many of those proposals have come back with a quality.</summary>
    public long Scored { get; internal set; }

    /// <summary>The mean primary quality of the programs written under this instruction.</summary>
    /// <remarks>Zero while <see cref="Scored"/> is zero; an unscored instruction is unexplored, not bad.</remarks>
    public double MeanQuality => Scored == 0 ? 0.0 : _totalQuality / Scored;

    internal double TotalQuality
    {
        get => _totalQuality;
        set => _totalQuality = value;
    }

    private double _totalQuality;

    internal string Identity() => string.Join(
        "|",
        Id,
        ParentId ?? "-",
        Generation.ToString(CultureInfo.InvariantCulture),
        Uses.ToString(CultureInfo.InvariantCulture),
        Scored.ToString(CultureInfo.InvariantCulture),
        EvolutionHash.EncodeDouble(_totalQuality));
}
