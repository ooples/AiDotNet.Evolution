using System.Globalization;
using AiDotNet.Evolution;

namespace AiDotNet.Evolution.Programs;

/// <summary>
/// Settings for meta-prompt evolution: the system instructions evolve alongside the programs
/// (V1-53, #166).
/// </summary>
/// <remarks>
/// AlphaEvolve credits this component in its ablations. OpenEvolve 0.3.2 declares
/// <c>use_meta_prompting</c> and <c>meta_prompt_weight</c> and its own config comment says the
/// feature is not implemented, so there is no upstream behaviour to match - only the paper's
/// description.
/// </remarks>
public sealed class ProgramMetaPromptOptions
{
    /// <summary>Gets or sets whether instructions evolve. Off by default, so no model is contacted.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets how many generations pass between instruction proposals.</summary>
    /// <remarks>
    /// Every proposal is a model call charged to the run, so this is the knob that bounds the meta
    /// level's share of the run's tokens. At 25 with a 1,000-evaluation run it is about 40 calls.
    /// </remarks>
    public int ProposeEveryGenerations { get; set; } = 25;

    /// <summary>Gets or sets how many instructions the archive keeps.</summary>
    /// <remarks>
    /// When it is full the worst-scoring scored instruction is dropped. The seed instruction is
    /// never dropped, so a run can always fall back to the instruction it started from.
    /// </remarks>
    public int MaxArchiveSize { get; set; } = 8;

    /// <summary>Gets or sets how an instruction is chosen for a proposal.</summary>
    public ProgramInstructionSelection Selection { get; set; } = ProgramInstructionSelection.EpsilonGreedy;

    /// <summary>Gets or sets which way the run's primary quality is better.</summary>
    /// <remarks>
    /// The archive orders instructions by mean quality on a "better is higher" scale, so a
    /// minimizing run's measurements are inverted as they are observed. Leaving that to each caller
    /// would make a minimizing run prefer the instruction that produced the worst programs, and
    /// silently - the archive would look perfectly well ordered.
    /// </remarks>
    public EvolutionOptimizationDirection Direction { get; set; } = EvolutionOptimizationDirection.Maximize;

    /// <summary>Gets or sets the chance of ignoring the leader under <see cref="ProgramInstructionSelection.EpsilonGreedy"/>.</summary>
    public double ExplorationProbability { get; set; } = 0.2;

    /// <summary>Gets or sets the cost units charged to the run's ledger for one instruction proposal.</summary>
    public decimal ProposalCostUnits { get; set; } = 1m;

    /// <summary>Gets or sets the longest instruction the evolver will accept from the model.</summary>
    /// <remarks>
    /// A model asked for instructions sometimes answers with a program instead. A bound keeps one
    /// bad answer from becoming the prompt for the rest of the run; the answer is refused and the
    /// archive is left alone.
    /// </remarks>
    public int MaxInstructionChars { get; set; } = 4_000;

    /// <summary>Gets or sets the descriptor prefix the meta level reports its own cost under.</summary>
    public string MetricPrefix { get; set; } = "meta_prompt_";

    /// <summary>Throws if any setting is outside its documented range.</summary>
    public void Validate()
    {
        if (ProposeEveryGenerations is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(ProposeEveryGenerations), ProposeEveryGenerations,
                "ProposeEveryGenerations must be between 1 and 1000000.");
        if (MaxArchiveSize is < 2 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaxArchiveSize), MaxArchiveSize,
                "MaxArchiveSize must be between 2 and 256; one entry would leave nothing to select between.");
        if (!Enum.IsDefined(typeof(ProgramInstructionSelection), Selection))
            throw new ArgumentOutOfRangeException(nameof(Selection), Selection, "Unknown instruction selection.");
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), Direction))
            throw new ArgumentOutOfRangeException(nameof(Direction), Direction, "Unknown optimization direction.");
        if (double.IsNaN(ExplorationProbability) || ExplorationProbability < 0 || ExplorationProbability > 1)
            throw new ArgumentOutOfRangeException(nameof(ExplorationProbability), ExplorationProbability,
                "ExplorationProbability must be between 0 and 1.");
        if (ProposalCostUnits < 0m || ProposalCostUnits > 1_000_000m)
            throw new ArgumentOutOfRangeException(nameof(ProposalCostUnits), ProposalCostUnits,
                "ProposalCostUnits must be between 0 and 1000000.");
        if (MaxInstructionChars is < 64 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(MaxInstructionChars), MaxInstructionChars,
                "MaxInstructionChars must be between 64 and 100000.");
        if (string.IsNullOrWhiteSpace(MetricPrefix) || MetricPrefix.Length > 64)
            throw new ArgumentException("MetricPrefix must be nonempty and at most 64 characters.", nameof(MetricPrefix));
    }

    /// <summary>Returns an independent copy, so a caller cannot mutate settings a run is using.</summary>
    public ProgramMetaPromptOptions Clone() => new()
    {
        Enabled = Enabled,
        ProposeEveryGenerations = ProposeEveryGenerations,
        MaxArchiveSize = MaxArchiveSize,
        Selection = Selection,
        Direction = Direction,
        ExplorationProbability = ExplorationProbability,
        ProposalCostUnits = ProposalCostUnits,
        MaxInstructionChars = MaxInstructionChars,
        MetricPrefix = MetricPrefix,
    };

    /// <summary>A stable identity for the settings, so a checkpoint from other settings is refused.</summary>
    internal string Identity() => string.Join(
        "|",
        Enabled ? "1" : "0",
        ProposeEveryGenerations.ToString(CultureInfo.InvariantCulture),
        MaxArchiveSize.ToString(CultureInfo.InvariantCulture),
        ((int)Selection).ToString(CultureInfo.InvariantCulture),
        ((int)Direction).ToString(CultureInfo.InvariantCulture),
        ExplorationProbability.ToString("R", CultureInfo.InvariantCulture),
        // Scale-normalized: 1m and 1.000m are the same budget, so they must not produce two
        // different identities and make a checkpoint from one refuse to restore under the other.
        (ProposalCostUnits / 1.000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
        MaxInstructionChars.ToString(CultureInfo.InvariantCulture),
        MetricPrefix);
}
